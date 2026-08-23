namespace CoreLib.Tests;

/// <summary>
/// The test classes that bind real loopback listeners and dial them.
///
/// <para>xUnit runs test classes in parallel by default, and three classes each standing up
/// several devices with listeners, dial loops and background reconcile passes contend for the
/// thread pool rather than for anything they are trying to test. Under load that showed up as
/// <c>Dropping_links_leaves_the_device_listening</c> and
/// <c>Three_devices_each_hold_a_link_to_both_of_the_others</c> failing together on a run that
/// passed three times in a row on an idle machine.</para>
///
/// <para>A flaky test is worse than a missing one, because it teaches you to re-run rather than to
/// look. These share a collection so they run one class at a time; inside a class xUnit is already
/// serial.</para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class LoopbackCollection
{
    public const string Name = "loopback sockets";
}
