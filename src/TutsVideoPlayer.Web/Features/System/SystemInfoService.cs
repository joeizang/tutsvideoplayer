using System.Runtime.InteropServices;
using JsxCore.Hosting;
using TutsVideoPlayer.Infrastructure.FileSystem;
using TutsVideoPlayer.Web.Models;

namespace TutsVideoPlayer.Web.Features.System;

public sealed class SystemInfoService(MediaFileLocator mediaFileLocator)
{
    private const string FixtureFileName = "demo-fixture.mp4";

    public SystemInfoModel CreateModel()
    {
        var fixtureAvailable = mediaFileLocator.TryResolve(FixtureFileName, out var fixturePath);
        var fixtureSize = fixtureAvailable ? new FileInfo(fixturePath).Length : 0;

        return new SystemInfoModel(
            ApplicationName: "Tuts Video Player",
            RuntimeDescription: RuntimeInformation.FrameworkDescription,
            OperatingSystem: RuntimeInformation.OSDescription,
            FixtureMediaUrl: $"/media/demo/{FixtureFileName}",
            FixtureSizeBytes: fixtureSize,
            FixtureAvailable: fixtureAvailable);
    }
}
