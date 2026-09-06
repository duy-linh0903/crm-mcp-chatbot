namespace Backend_MCP.Services.Implementations;

public class TaskChatService : ITaskChatService
{
    private readonly ITaskChatMessageRepository _taskChatMessageRepository;
    private readonly ITaskRepository _taskRepository;
    private readonly IHubContext<NotificationHub> _hubContext;

    public TaskChatService(
        ITaskChatMessageRepository taskChatMessageRepository,
        ITaskRepository taskRepository,
        IHubContext<NotificationHub> hubContext)
    {
        _taskChatMessageRepository = taskChatMessageRepository;
        _taskRepository = taskRepository;
        _hubContext = hubContext;
    }

    public async Task<List<TaskChatMessageResponse>> GetHistoryAsync(GetTaskChatHistoryRequest request, CancellationToken cancellationToken = default)
    {
        var messages = await _taskChatMessageRepository.GetByTaskIdAsync(request.TaskId, cancellationToken);
        return messages.Select(TaskChatMessageResponse.FromEntity).ToList();
    }

    public async Task<TaskChatMessageResponse> CreateAsync(CreateTaskChatMessageRequest request, CancellationToken cancellationToken = default)
    {
        var message = new TaskChatMessage
        {
            TaskId = request.TaskId,
            AuthorId = request.AuthorId,
            AuthorName = request.AuthorName,
            Message = request.Message,
            CreatedAt = DateTime.UtcNow
        };

        await _taskChatMessageRepository.CreateAsync(message, cancellationToken);
        var response = TaskChatMessageResponse.FromEntity(message);
        var task = await _taskRepository.GetByIdAsync(request.TaskId, cancellationToken);
        if (task is not null)
        {
            var recipientIds = task.SupervisorIds
                .Append(task.CreatorId)
                .Append(task.AssigneeId)
                .Where(id => !string.IsNullOrWhiteSpace(id) && id != request.AuthorId)
                .Select(id => id!)
                .Distinct()
                .ToList();

            if (recipientIds.Count > 0)
            {
                await _hubContext.Clients.Users(recipientIds).SendAsync("ReceiveMessage", response, cancellationToken);
            }
        }

        return response;
    }
}