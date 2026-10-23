using System.Diagnostics;
using BetterVitals.Core.Sensors;
using BetterVitals.Sensors.Native;
using BetterVitals.Sensors.Native.Drives;
using Xunit;

namespace BetterVitals.Tests.Native;

/// <summary>The drive node's rules — never touch a sleeping HDD, keep the handle, reopen after failures — against a fake storage stack.</summary>
public sealed class DriveNodeTests
{
    private sealed class FakeIo : IStorageIo
    {
        public bool OpenOk = true;
        public bool InD0 = true;
        public bool ReadOk = true;
        public float Temp = 41;
        public int Opens, Closes, D0Checks, Reads;

        public nint Open(int physicalIndex)
        {
            Opens++;
            return OpenOk ? 1000 + Opens : 0;
        }

        public void Close(nint handle) => Closes++;

        public bool IsInD0(nint handle)
        {
            D0Checks++;
            return InD0;
        }

        public bool TryReadTemperature(nint handle, TemperatureMethod method, out float celsius)
        {
            Reads++;
            celsius = ReadOk ? Temp : float.NaN;
            return ReadOk;
        }
    }

    private static readonly long Second = Stopwatch.Frequency;

    private static DriveTemperatureModule.DriveNode Node(FakeIo io, bool rotating, nint handle = 7)
        => new(io, 0, "Test drive", handle, rotating, TemperatureMethod.DeviceProperty, float.NaN);

    [Fact]
    public void A_sleeping_rotating_drive_is_not_read_and_reads_again_as_soon_as_it_is_awake()
    {
        var io = new FakeIo { InD0 = false };
        var node = Node(io, rotating: true);

        node.Refresh(10 * Second);
        node.Refresh(40 * Second);

        Assert.Equal(0, io.Reads);                      // not a single command reached the drive
        Assert.Equal(2, io.D0Checks);
        Assert.True(float.IsNaN(node.Read(0)));

        io.InD0 = true;
        node.Refresh(70 * Second);

        Assert.Equal(1, io.Reads);
        Assert.Equal(41f, node.Read(0));
    }

    [Fact]
    public void A_solid_state_drive_is_read_without_asking_about_standby()
    {
        var io = new FakeIo { InD0 = false };
        var node = Node(io, rotating: false);

        node.Refresh(10 * Second);

        Assert.Equal(0, io.D0Checks);
        Assert.Equal(1, io.Reads);
        Assert.Equal(41f, node.Read(0));
    }

    [Fact]
    public void The_handle_stays_open_between_reads()
    {
        var io = new FakeIo();
        var node = Node(io, rotating: false);

        for (var i = 0; i < 50; i++) node.Refresh((10 + i) * Second);

        Assert.Equal(0, io.Opens);
        Assert.Equal(0, io.Closes);
        Assert.Equal(50, io.Reads);
    }

    [Fact]
    public void Three_failures_in_a_row_close_the_handle_and_it_is_reopened_after_thirty_seconds()
    {
        var io = new FakeIo { ReadOk = false };
        var node = Node(io, rotating: false);

        node.Refresh(100 * Second);
        node.Refresh(101 * Second);
        Assert.Equal(0, io.Closes);
        node.Refresh(102 * Second);                     // third failure
        Assert.Equal(1, io.Closes);
        Assert.True(float.IsNaN(node.Read(0)));

        node.Refresh(110 * Second);                     // too early
        Assert.Equal(0, io.Opens);
        Assert.Equal(3, io.Reads);

        io.ReadOk = true;
        node.Refresh(133 * Second);                     // 31 s after the close: reopen and read
        Assert.Equal(1, io.Opens);
        Assert.Equal(41f, node.Read(0));
    }

    [Fact]
    public void A_drive_that_cannot_be_reopened_is_tried_again_only_every_thirty_seconds()
    {
        var io = new FakeIo { ReadOk = false, OpenOk = false };
        var node = Node(io, rotating: false);
        for (var i = 0; i < 3; i++) node.Refresh((100 + i) * Second); // closes the handle

        for (var s = 103; s < 165; s++) node.Refresh(s * Second);

        Assert.Equal(2, io.Opens); // at about t+30 s and t+60 s: not once a second
        Assert.True(float.IsNaN(node.Read(0)));
    }

    [Fact]
    public void Release_closes_the_handle()
    {
        var io = new FakeIo();
        var node = Node(io, rotating: false);

        node.Release();

        Assert.Equal(1, io.Closes);
    }

    [Fact]
    public void Handles_of_drives_that_got_no_sensor_are_closed_and_the_others_kept()
    {
        var io = new FakeIo();
        var drives = new List<ProbedDrive>
        {
            new() { PhysicalIndex = 0, Model = "KINGSTON SKC3000D2048G", Method = TemperatureMethod.NvmeHealthLog, Temperature = 40, Handle = 11 },
            new() { PhysicalIndex = 1, Model = "Samsung SSD 850 EVO 500GB", Method = TemperatureMethod.None, Handle = 12 },     // does not answer
            new() { PhysicalIndex = 2, Model = "ST4000DM004-2CV104", Rotating = true, Method = TemperatureMethod.DeviceProperty, Temperature = 40, Handle = 13 },
        };
        var builder = new InventoryBuilder(Up.Index(Up.S(HardwareKind.Storage, "ST4000DM004-2CV104", SensorKind.Temperature, "Temperature"))); // covers the HDD

        new DriveTemperatureModule(() => drives, io).Discover(builder);

        Assert.Equal(2, io.Closes);                     // the Samsung (no answer) and the Seagate (covered)
        Assert.Equal(11, drives[0].Handle);
        Assert.Equal(0, drives[1].Handle);
        Assert.Equal(0, drives[2].Handle);
        Assert.Single(builder.Sensors);
    }
}
