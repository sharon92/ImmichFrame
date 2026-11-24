using ImmichFrame.WebApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ImmichFrame.WebApi.Controllers;

[ApiController, Authorize, Route(CloudflareFrame.Prefix)]
public sealed class CloudflareFrameController(CloudflareFrame cloud) : ControllerBase
{
    public record CheckInRequest(Guid Frame, string? Current);

    [HttpPost("check-in")]
    [RequestSizeLimit(2048)]
    public IActionResult CheckIn(CheckInRequest request)
    {
        if (request.Frame == Guid.Empty || request.Current != null && !CloudflareFrame.IsPhotoKey(request.Current))
            return BadRequest("Invalid playback status.");

        return cloud.CheckIn(request.Frame, request.Current, out var command)
            ? Ok(new { command })
            : Conflict("A different frame is already online.");
    }
}
