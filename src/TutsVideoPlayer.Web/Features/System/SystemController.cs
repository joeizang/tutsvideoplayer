using Microsoft.AspNetCore.Mvc;
using TutsVideoPlayer.Web.Models;

namespace TutsVideoPlayer.Web.Features.System;

[ApiController]
[Route("api/v1/system")]
public sealed class SystemController(SystemInfoService systemInfo) : ControllerBase
{
    [HttpGet("info")]
    public ActionResult<SystemInfoModel> GetInfo() => Ok(systemInfo.CreateModel());
}
