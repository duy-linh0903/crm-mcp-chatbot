namespace Backend_MCP.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class TasksController : ControllerBase
{
    private readonly ITaskService _taskService;

    public TasksController(ITaskService taskService)
    {
        _taskService = taskService;
    }

    [HttpGet]
    public async Task<ActionResult<List<TaskItemResponse>>> GetAll(CancellationToken cancellationToken)
    {
        return Ok(await _taskService.GetAllAsync(cancellationToken));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<TaskItemResponse>> GetById(string id, CancellationToken cancellationToken)
    {
        var task = await _taskService.GetByIdAsync(id, cancellationToken);
        return task is null ? NotFound() : Ok(task);
    }

    [HttpGet("department/{departmentId}")]
    public async Task<ActionResult<List<TaskItemResponse>>> GetByDepartment(string departmentId, CancellationToken cancellationToken)
    {
        return Ok(await _taskService.GetByDepartmentAsync(departmentId, cancellationToken));
    }

    [HttpGet("user/{userId}")]
    public async Task<ActionResult<List<TaskItemResponse>>> GetByUser(string userId, CancellationToken cancellationToken)
    {
        return Ok(await _taskService.GetByUserAsync(userId, cancellationToken));
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<TaskItemResponse>> Create([FromBody] CreateTaskRequest request, CancellationToken cancellationToken)
    {
        request.CreatorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var task = await _taskService.CreateAsync(request, cancellationToken);
        return Ok(task);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<TaskItemResponse>> Update(string id, [FromBody] UpdateTaskRequest request, CancellationToken cancellationToken)
    {
        var task = await _taskService.UpdateAsync(id, request, cancellationToken);
        return task is null ? NotFound() : Ok(task);
    }

    [HttpPost("{id}/assign")]
    public async Task<ActionResult<TaskItemResponse>> Assign(string id, [FromBody] AssignTaskRequest request, CancellationToken cancellationToken)
    {
        var task = await _taskService.AssignAsync(id, request, cancellationToken);
        return task is null ? NotFound() : Ok(task);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken)
    {
        var deleted = await _taskService.DeleteAsync(id, cancellationToken);
        return deleted ? NoContent() : NotFound();
    }
}