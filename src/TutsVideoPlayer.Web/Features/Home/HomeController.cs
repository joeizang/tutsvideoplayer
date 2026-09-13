using JsxCore;
using JsxCore.Hosting;
using JsxCore.Mvc;
using Microsoft.AspNetCore.Mvc;
using TutsVideoPlayer.Web.Features.System;

namespace TutsVideoPlayer.Web.Features.Home;

public sealed class HomeController(SystemInfoService systemInfo) : Controller
{
    [HttpGet("/")]
    public IActionResult Index() => this.Jsx("Home/Index", systemInfo.CreateModel(), RenderMode.ServerAndClient);
}
