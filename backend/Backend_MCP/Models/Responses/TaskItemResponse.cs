namespace Backend_MCP.Models.Responses;

public class TaskItemResponse
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string Priority { get; set; } = string.Empty;

    public string DepartmentId { get; set; } = string.Empty;

    public string? CreatorId { get; set; }

    public string? AssigneeId { get; set; }

    public List<string> SupervisorIds { get; set; } = [];

    public DateTime DueDate { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public static TaskItemResponse FromEntity(TaskItem entity)
    {
        return new TaskItemResponse
        {
            Id = entity.Id,
            Title = entity.Title,
            Description = entity.Description,
            Status = entity.Status.ToString(),
            Priority = entity.Priority.ToString(),
            DepartmentId = entity.DepartmentId,
            CreatorId = entity.CreatorId,
            AssigneeId = entity.AssigneeId,
            SupervisorIds = entity.SupervisorIds,
            DueDate = entity.DueDate,
            CreatedAt = entity.CreatedAt,
            CompletedAt = entity.CompletedAt
        };
    }
}