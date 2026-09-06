using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Authorization;

namespace Backend_MCP.Hubs;

[Authorize]
public class NotificationHub : Hub
{
}
