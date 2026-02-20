using System.Reflection;
using Revie.App.Services;
using Xunit;

namespace Revie.Tests;

public sealed class VideoCaptureServiceMicrophoneParsingTests
{
    [Fact]
    public void ParseDirectShowAudioDevices_ParsesModernFfmpegOutput()
    {
        const string output = """
            [dshow @ 000001] "Meta Quest 3" (video)
            [dshow @ 000001]   Alternative name "@device_sw_{...}"
            [dshow @ 000001] "Headset Microphone (Oculus Virtual Audio Device)" (audio)
            [dshow @ 000001]   Alternative name "@device_cm_{...}"
            Error opening input file dummy.
            """;

        var devices = InvokeParser(output);

        Assert.Single(devices);
        Assert.Equal("Headset Microphone (Oculus Virtual Audio Device)", devices[0]);
    }

    [Fact]
    public void ParseDirectShowAudioDevices_ParsesLegacySectionOutput()
    {
        const string output = """
            [dshow @ 000001] DirectShow audio devices
            [dshow @ 000001] "Microphone (USB Audio Device)"
            [dshow @ 000001]   Alternative name "@device_cm_{...}"
            [dshow @ 000001] "Microphone (USB Audio Device)"
            [dshow @ 000001] DirectShow video devices
            [dshow @ 000001] "Integrated Camera"
            """;

        var devices = InvokeParser(output);

        Assert.Single(devices);
        Assert.Equal("Microphone (USB Audio Device)", devices[0]);
    }

    private static IReadOnlyList<string> InvokeParser(string output)
    {
        var parser = typeof(VideoCaptureService).GetMethod(
            "ParseDirectShowAudioDevices",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(parser);
        var result = parser!.Invoke(null, new object?[] { output });
        var devices = Assert.IsAssignableFrom<IReadOnlyList<string>>(result);
        return devices;
    }
}
