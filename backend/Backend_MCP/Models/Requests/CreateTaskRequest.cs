namespace Backend_MCP.Models.Requests;

public class CreateTaskRequest
{
    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string DepartmentId { get; set; } = string.Empty;

    public string? CreatorId { get; set; }

    public string? AssigneeId { get; set; }

    public List<string> SupervisorIds { get; set; } = [];

    public DateTime DueDate { get; set; }

    public TaskPriority Priority { get; set; } = TaskPriority.Medium;
}