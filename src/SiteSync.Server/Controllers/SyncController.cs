using Microsoft.AspNetCore.Mvc;
using SiteSync.Server.Domain;
using SiteSync.Server.Sync;

namespace SiteSync.Server.Controllers;

[ApiController]
[Route("api")]
public class SyncController : ControllerBase
{
    private readonly JobService _jobs;

    public SyncController(JobService jobs) => _jobs = jobs;

    /// <summary>Start a preview (dry run): discovers both trees and computes the diff.</summary>
    [HttpPost("sync/preview")]
    public IActionResult Preview([FromBody] SyncJobRequest request)
    {
        try
        {
            var job = _jobs.StartPreview(request);
            return Ok(SyncJobSummary.From(job));
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    public record ApplyRequest(Dictionary<string, ConflictResolution>? Resolutions);

    /// <summary>Apply a previewed job, with optional per-item conflict resolutions / skips.</summary>
    [HttpPost("sync/{jobId}/apply")]
    public IActionResult Apply(string jobId, [FromBody] ApplyRequest? request)
    {
        try
        {
            var job = _jobs.StartApply(jobId, request?.Resolutions);
            return Ok(SyncJobSummary.From(job));
        }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("sync/{jobId}/cancel")]
    public IActionResult Cancel(string jobId) =>
        _jobs.Cancel(jobId) ? Ok() : NotFound(new { error = "No running job with that id." });

    [HttpGet("jobs")]
    public IActionResult Jobs() => Ok(_jobs.ListJobs());

    [HttpGet("jobs/{jobId}")]
    public IActionResult Job(string jobId)
    {
        var job = _jobs.GetJob(jobId);
        if (job == null) return NotFound();
        return Ok(SyncJobSummary.From(job));
    }

    /// <summary>Full diff list for the review screen (can be large; fetched separately from the summary).</summary>
    [HttpGet("jobs/{jobId}/diff")]
    public IActionResult Diff(string jobId, [FromQuery] bool includeUnchanged = false)
    {
        var job = _jobs.GetJob(jobId);
        if (job == null) return NotFound();
        var diffs = includeUnchanged ? job.Diffs : job.Diffs.Where(d => d.Status != DiffStatus.Unchanged);
        return Ok(diffs);
    }

    [HttpGet("jobs/{jobId}/log")]
    public IActionResult Log(string jobId)
    {
        var job = _jobs.GetJob(jobId);
        if (job == null) return NotFound();
        lock (job.Log) return Ok(job.Log.ToList());
    }
}
