using System.Reflection;
using Deluno.Platform.Contracts;

namespace Deluno.Platform.Tests.Contracts;

/// <summary>
/// A settings patch changes only what it carries. For every setting the patch can carry: a patch without it leaves the saved value
/// as it was, and a patch with it saves the patch's value. Written over the records' own properties, so a setting added later is held
/// to the same rule without anyone listing it here.
/// </summary>
public sealed class PlatformSettingsPatchMergerTests
{
    // Properties the merger does not inherit: the two API keys are written as given (a null patch value clears nothing here, the
    // repository keeps the stored secret), and the sharing targets follow the sharing mode (tested on their own below).
    private static readonly HashSet<string> NotInherited = ["MetadataTmdbApiKey", "MetadataOmdbApiKey", "MdbListApiKey", "SharingForHours", "SharingUntilRatio"];

    private static object Value(Type type, int seed)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(string)) return $"value-{seed}";
        if (underlying == typeof(bool)) return seed % 2 == 0;
        if (underlying == typeof(int)) return 100 + seed;
        if (underlying == typeof(long)) return 100L + seed;
        if (underlying == typeof(double)) return 1.5 + seed;
        if (underlying == typeof(decimal)) return 1.5m + seed;
        if (underlying == typeof(DateTimeOffset)) return DateTimeOffset.UnixEpoch.AddDays(seed);
        throw new NotSupportedException($"No test value for {type}.");
    }

    private static PlatformSettingsSnapshot Current()
    {
        var constructor = typeof(PlatformSettingsSnapshot).GetConstructors().Single();
        var arguments = constructor.GetParameters().Select((parameter, index) => Value(parameter.ParameterType, index)).ToArray();
        return (PlatformSettingsSnapshot)constructor.Invoke(arguments);
    }

    private static PatchPlatformSettingsRequest PatchWith(string property, object value)
    {
        var constructor = typeof(PatchPlatformSettingsRequest).GetConstructors().Single();
        var arguments = constructor.GetParameters().Select(parameter => parameter.Name == property ? value : null).ToArray();
        return (PatchPlatformSettingsRequest)constructor.Invoke(arguments);
    }

    private static object? Read(object target, string name) => target.GetType().GetProperty(name)!.GetValue(target);

    public static TheoryData<string> PatchedSettings()
    {
        var data = new TheoryData<string>();
        foreach (var parameter in typeof(PatchPlatformSettingsRequest).GetConstructors().Single().GetParameters())
        {
            data.Add(parameter.Name!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PatchedSettings))]
    public void A_patch_with_a_setting_saves_it_and_a_patch_without_it_leaves_the_saved_one(string property)
    {
        var current = Current();
        var parameter = typeof(PatchPlatformSettingsRequest).GetConstructors().Single().GetParameters().Single(p => p.Name == property);
        // The helper kind is the one setting the merger normalises on the way through: only a name it knows survives.
        var patched = property == nameof(PatchPlatformSettingsRequest.CloudflareHelperKind) ? CloudflareHelperKinds.Byparr : Value(parameter.ParameterType, 900);

        var withIt = PlatformSettingsPatchMerger.Apply(current, PatchWith(property, patched));
        var without = PlatformSettingsPatchMerger.Apply(current, PatchWith("none", patched));

        if (NotInherited.Contains(property))
        {
            return;
        }

        Assert.Equal(patched, Read(withIt, property));
        Assert.Equal(
            property == nameof(PatchPlatformSettingsRequest.CloudflareHelperKind) ? CloudflareHelperKinds.Normalize(current.CloudflareHelperKind) : Read(current, property),
            Read(without, property));
    }

    [Fact]
    public void A_patch_that_sets_the_sharing_mode_takes_its_targets_and_one_that_does_not_keeps_the_saved_ones()
    {
        var current = Current();

        var touching = PlatformSettingsPatchMerger.Apply(current, new PatchPlatformSettingsRequest(SharingMode: "hours", SharingForHours: 7));
        Assert.Equal(7, touching.SharingForHours);
        Assert.Null(touching.SharingUntilRatio);

        var leaving = PlatformSettingsPatchMerger.Apply(current, new PatchPlatformSettingsRequest(SharingForHours: 7));
        Assert.Equal(current.SharingForHours, leaving.SharingForHours);
        Assert.Equal(current.SharingUntilRatio, leaving.SharingUntilRatio);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_smallest_file_to_force_of_nothing_saves_the_100_MB_it_says_it_will_use(int sent)
    {
        var merged = PlatformSettingsPatchMerger.Apply(Current(), new PatchPlatformSettingsRequest(ForceImportMinimumMegabytes: sent));

        Assert.Equal(100, merged.ForceImportMinimumMegabytes);
    }

    [Fact]
    public void Every_setting_a_patch_carries_is_one_the_update_carries_too()
    {
        var update = typeof(UpdatePlatformSettingsRequest).GetProperties().Select(property => property.Name).ToHashSet();

        Assert.All(typeof(PatchPlatformSettingsRequest).GetConstructors().Single().GetParameters(), parameter => Assert.Contains(parameter.Name!, update));
    }
}
