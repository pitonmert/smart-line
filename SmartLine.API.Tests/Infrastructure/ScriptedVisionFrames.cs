using Microsoft.AspNetCore.Http;
using SmartLine.API.Features.Inspection;
using SmartLine.API.Shared;

namespace SmartLine.API.Tests.Infrastructure;

public sealed class ScriptedVisionFrames : IVisionFrames
{
    public byte[]? Jpeg { get; set; }
    public int Reads { get; private set; }
    public string? LastRole { get; private set; }
    public bool FailStill { get; set; }

    public void Reset()
    {
        Jpeg = null;
        Reads = 0;
        LastRole = null;
        FailStill = false;
    }

    public Task<VisionCapturedFrame> ReadAsync(string role, CancellationToken ct)
    {
        Reads++;
        LastRole = role;
        if (FailStill)
        {
            throw new ApiException(
                StatusCodes.Status409Conflict,
                ErrorCodes.DatasetInvalid,
                "High-quality still capture failed and preview fallback is disabled."
            );
        }

        if (Jpeg is null)
        {
            throw new ApiException(
                StatusCodes.Status503ServiceUnavailable,
                ErrorCodes.DatasetInvalid,
                "Vision is not configured. Set Vision:HealthUrl before capturing from the camera."
            );
        }

        return Task.FromResult(new VisionCapturedFrame(Jpeg, DateTime.UtcNow, 640, 480, role, "still_capture", "esp32_capture", false));
    }
}
