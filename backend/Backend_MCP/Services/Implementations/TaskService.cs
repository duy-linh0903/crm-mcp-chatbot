namespace Backend_MCP.Services.Implementations;

public class TaskService : ITaskService
{
    private readonly ITaskRepository _taskRepository;
    private readonly IUserRepository _userRepository;
    private readonly IHubContext<NotificationHub> _hubContext;

    public TaskService(ITaskRepository taskRepository, IUserRepository userRepository, IHubContext<NotificationHub> hubContext)
    {
        _taskRepository = taskRepository;
        _userRepository = userRepository;
        _hubContext = hubContext;
    }

    public async Task<List<TaskItemResponse>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var tasks = await _taskRepository.GetAllAsync(cancellationToken);
        return tasks.Select(TaskItemResponse.FromEntity).ToList();
    }

    public async Task<TaskItemResponse?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var task = await _taskRepository.GetByIdAsync(id, cancellationToken);
        return task is null ? null : TaskItemResponse.FromEntity(task);
    }

    public async Task<List<TaskItemResponse>> GetByDepartmentAsync(string departmentId, CancellationToken cancellationToken = default)
    {
        var tasks = await _taskRepository.GetByDepartmentIdAsync(departmentId, cancellationToken);
        return tasks.Select(TaskItemResponse.FromEntity).ToList();
    }

    public async Task<List<TaskItemResponse>> GetByUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        var tasks = await _taskRepository.GetByAssigneeIdAsync(userId, cancellationToken);
        return tasks.Select(TaskItemResponse.FromEntity).ToList();
    }

    public async Task<TaskItemResponse> CreateAsync(CreateTaskRequest request, CancellationToken cancellationToken = default)
    {
        var task = new TaskItem
        {
            Title = request.Title,
            Description = request.Description,
            DepartmentId = request.DepartmentId,
            CreatorId = request.CreatorId,
            AssigneeId = request.AssigneeId,
            SupervisorIds = request.SupervisorIds,
            DueDate = request.DueDate,
            Priority = request.Priority,
            Status = TaskStatus.Todo,
            CreatedAt = DateTime.UtcNow
        };

        await _taskRepository.CreateAsync(task, cancellationToken);
        if (!string.IsNullOrWhiteSpace(task.AssigneeId))
        {
            await _hubContext.Clients.User(task.AssigneeId).SendAsync("ReceiveAlert", new
            {
                type = "TaskCreated",
                task = TaskItemResponse.FromEntity(task)
            }, cancellationToken);
        }
        return TaskItemResponse.FromEntity(task);
    }

    public async Task<TaskItemResponse?> UpdateAsync(string id, UpdateTaskRequest request, CancellationToken cancellationToken = default)
    {
        var task = await _taskRepository.GetByIdAsync(id, cancellationToken);
        if (task is null)
        {
            return null;
        }

        task.Title = request.Title;
        task.Description = request.Description;
        task.DepartmentId = request.DepartmentId;
        task.AssigneeId = request.AssigneeId;
        task.SupervisorIds = request.SupervisorIds;
        task.DueDate = request.DueDate;
        task.Status = request.Status;
        task.Priority = request.Priority;
        task.CompletedAt = request.Status == TaskStatus.Completed ? DateTime.UtcNow : null;

        await _taskRepository.ReplaceAsync(task, cancellationToken);
        return TaskItemResponse.FromEntity(task);
    }

    public async Task<TaskItemResponse?> UpdateStatusAsync(string id, TaskStatus status, CancellationToken cancellationToken = default)
    {
        var task = await _taskRepository.GetByIdAsync(id, cancellationToken);
        if (task is null)
        {
            return null;
        }

        task.Status = status;
        task.CompletedAt = status == TaskStatus.Completed ? DateTime.UtcNow : null;

        await _taskRepository.ReplaceAsync(task, cancellationToken);
        var recipientIds = task.SupervisorIds
            .Append(task.CreatorId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct()
            .ToList();

        if (recipientIds.Count > 0)
        {
            await _hubContext.Clients.Users(recipientIds).SendAsync("ReceiveAlert", new
            {
                type = "TaskStatusUpdated",
                task = TaskItemResponse.FromEntity(task)
            }, cancellationToken);
        }
        return TaskItemResponse.FromEntity(task);
    }

    public async Task<List<TaskItemResponse>> GetOverdueAsync(string? departmentId = null, int? limit = null, CancellationToken cancellationToken = default)
    {
        var overdueTasks = await _taskRepository.GetOverdueAsync(cancellationToken: cancellationToken);

        if (!string.IsNullOrWhiteSpace(departmentId))
        {
            overdueTasks = overdueTasks.Where(task => task.DepartmentId == departmentId).ToList();
        }

        if (limit.HasValue && limit.Value > 0)
        {
            overdueTasks = overdueTasks.Take(limit.Value).ToList();
        }

        return overdueTasks.Select(TaskItemResponse.FromEntity).ToList();
    }

    public async Task<List<WorkloadSummaryResponse>> GetWorkloadSummaryAsync(GetWorkloadSummaryRequest request, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var tasks = new List<TaskItem>();

        if (!string.IsNullOrWhiteSpace(request.UserId))
        {
            tasks = await _taskRepository.GetByAssigneeIdAsync(request.UserId, cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(request.DepartmentId))
        {
            tasks = await _taskRepository.GetByDepartmentIdAsync(request.DepartmentId, cancellationToken);
        }
        else
        {
            tasks = await _taskRepository.GetAllAsync(cancellationToken);
        }

        var grouped = tasks
            .Where(task => !string.IsNullOrWhiteSpace(task.AssigneeId))
            .GroupBy(task => task.AssigneeId!)
            .Select(async group =>
            {
                var user = await _userRepository.GetByIdAsync(group.Key, cancellationToken);
                var userTasks = group.ToList();
                var completed = userTasks.Count(task => task.Status == TaskStatus.Completed);
                var inProgress = userTasks.Count(task => task.Status == TaskStatus.InProgress);
                var overdue = userTasks.Count(task => task.Status != TaskStatus.Completed && task.DueDate < now);

                return new WorkloadSummaryResponse
                {
                    UserId = group.Key,
                    UserName = user?.FullName ?? string.Empty,
                    TotalTasks = userTasks.Count,
                    CompletedTasks = completed,
                    InProgressTasks = inProgress,
                    OverdueTasks = overdue,
                    CompletionRate = userTasks.Count == 0 ? 0 : Math.Round((decimal)completed * 100 / userTasks.Count, 2)
                };
            });

        var results = await Task.WhenAll(grouped);
        return results.OrderByDescending(item => item.TotalTasks).ToList();
    }

    public async Task<TaskItemResponse?> AssignAsync(string id, AssignTaskRequest request, CancellationToken cancellationToken = default)
    {
        var task = await _taskRepository.GetByIdAsync(id, cancellationToken);
        if (task is null)
        {
            return null;
        }

        task.AssigneeId = request.AssigneeId;
        task.SupervisorIds = request.SupervisorIds;

        await _taskRepository.ReplaceAsync(task, cancellationToken);
        return TaskItemResponse.FromEntity(task);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var task = await _taskRepository.GetByIdAsync(id, cancellationToken);
        if (task is null)
        {
            return false;
        }

        await _taskRepository.DeleteAsync(id, cancellationToken);
        return true;
    }
}