namespace Backend_MCP.Services.Implementations;

public class AiChatService : IAiChatService
{
    private readonly IMcpToolService _mcpToolService;
    private readonly IChatService _chatService;
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly IMemoryCache _memoryCache;
    private readonly string[] _apiKeys;
    private int _nextApiKeyIndex;

    public AiChatService(
        IMcpToolService mcpToolService,
        IChatService chatService,
        HttpClient httpClient,
        IConfiguration configuration,
        IMemoryCache memoryCache)
    {
        _mcpToolService = mcpToolService;
        _chatService = chatService;
        _httpClient = httpClient;
        _configuration = configuration;
        _memoryCache = memoryCache;
        _apiKeys = configuration.GetSection("Gemini:ApiKey").Get<string[]>() ??
            [configuration["Gemini:ApiKey"] ?? string.Empty];
    }

    public async Task<ChatResponse> AskAsync(AskAiRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var session = await _chatService.EnsureSessionAsync(request.UserId, request.SessionId, request.SessionTitle, cancellationToken);
        await _chatService.SaveUserMessageAsync(request.UserId, session.SessionId, request.Message, cancellationToken);

        var contextMessages = await _chatService.GetContextMessagesAsync(session.SessionId, 20, cancellationToken);
        var contextText = contextMessages.Count == 0
            ? string.Empty
            : string.Join("\n", contextMessages.Select(message => $"[{message.Sender}] {message.Content}"));

        var contextPrompt = $"User Id hiện tại đang gọi hệ thống: {request.UserId}.";
        if (!string.IsNullOrWhiteSpace(request.TaskId))
        {
            contextPrompt += $" Context TaskId: {request.TaskId}.";
        }
        if (!string.IsNullOrWhiteSpace(request.DepartmentId))
        {
            contextPrompt += $" Context DepartmentId: {request.DepartmentId}.";
        }
        if (!string.IsNullOrWhiteSpace(contextText))
        {
            contextPrompt += $"\nLịch sử trò chuyện gần đây:\n{contextText}";
        }

        var toolsDeclaration = _mcpToolService.GetAvailableTools();

        var contentsList = new List<object>
        {
            new
            {
                role = "user",
                parts = new object[] { new { text = $"{contextPrompt}\nCâu hỏi: {request.Message}" } }
            }
        };

        string? lastFunctionName = null;
        var toolCallLogs = new List<McpToolCallLog>();

        const int maxRounds = 5;
        for (int round = 0; round < maxRounds; round++)
        {
            var requestBody = new
            {
                contents = contentsList.ToArray(),
                tools = toolsDeclaration
            };

            var response = await SendToGeminiWithKeyFallbackAsync(requestBody, cancellationToken);
            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("candidates", out var candidates) &&
                candidates.EnumerateArray().Any() &&
                candidates[0].TryGetProperty("content", out var content) &&
                content.TryGetProperty("parts", out var parts))
            {
                var firstPart = parts.EnumerateArray().FirstOrDefault();
                if (firstPart.ValueKind != JsonValueKind.Undefined && firstPart.TryGetProperty("functionCall", out var functionCall))
                {
                    var functionName = functionCall.GetProperty("name").GetString();
                    var args = functionCall.GetProperty("args");

                    if (string.IsNullOrWhiteSpace(functionName))
                    {
                        break;
                    }

                    lastFunctionName = functionName;

                    var mcpRequest = new JsonRpcRequest
                    {
                        Id = Guid.NewGuid().ToString(),
                        Method = functionName,
                        Parameters = args
                    };

                    var mcpResponse = await HandleToolWithCacheAsync(functionName, args, mcpRequest, cancellationToken);
                    if (functionName.Equals("get_task_chat_history", StringComparison.OrdinalIgnoreCase))
                    {
                        mcpResponse.Result = TruncateChatHistory(mcpResponse.Result, 20);
                    }
                    toolCallLogs.Add(new McpToolCallLog
                    {
                        ToolName = functionName,
                        Arguments = args.GetRawText(),
                        ExecutedAt = DateTime.UtcNow,
                        Result = mcpResponse.Result is null ? null : JsonSerializer.Serialize(mcpResponse.Result)
                    });

                    JsonElement partsClone;
                    try
                    {
                        partsClone = JsonSerializer.Deserialize<JsonElement>(parts.GetRawText());
                    }
                    catch
                    {
                        using var tmpDoc = JsonDocument.Parse(parts.GetRawText());
                        partsClone = tmpDoc.RootElement.Clone();
                    }

                    contentsList.Add(new { role = "model", parts = partsClone });
                    contentsList.Add(new
                    {
                        role = "user",
                        parts = new object[]
                        {
                            new
                            {
                                functionResponse = new
                                {
                                    name = functionName,
                                    response = new { data = mcpResponse.Result }
                                }
                            }
                        }
                    });

                    continue;
                }
            }

            var finalAnswer = ExtractTextFromGeminiResponse(root);
            var responsePayload = new ChatResponse
            {
                Answer = finalAnswer,
                ToolUsed = lastFunctionName,
                ProcessedAt = DateTime.UtcNow
            };

            await _chatService.SaveAssistantMessageAsync(request.UserId, session.SessionId, responsePayload.Answer, toolCallLogs, cancellationToken);
            return responsePayload;
        }

        var fallbackPayload = new ChatResponse
        {
            Answer = "AI did not return a final textual answer after multiple tool calls.",
            ToolUsed = null,
            ProcessedAt = DateTime.UtcNow
        };

        await _chatService.SaveAssistantMessageAsync(request.UserId, session.SessionId, fallbackPayload.Answer, toolCallLogs, cancellationToken);
        return fallbackPayload;
    }

    private async Task<JsonRpcResponse> HandleToolWithCacheAsync(
        string functionName,
        JsonElement args,
        JsonRpcRequest mcpRequest,
        CancellationToken cancellationToken)
    {
        var isStatisticsTool = functionName is "get_department_kpi" or "get_overdue_tasks" or "get_workload_summary";
        if (!isStatisticsTool)
        {
            return await _mcpToolService.HandleAsync(mcpRequest, cancellationToken);
        }

        var cacheKey = $"mcp:{functionName}:{args.GetRawText()}";
        if (_memoryCache.TryGetValue<JsonRpcResponse>(cacheKey, out var cachedResponse) && cachedResponse is not null)
        {
            return cachedResponse;
        }

        var response = await _mcpToolService.HandleAsync(mcpRequest, cancellationToken);
        _memoryCache.Set(cacheKey, response, TimeSpan.FromMinutes(5));
        return response;
    }

    private async Task<HttpResponseMessage> SendToGeminiWithKeyFallbackAsync(object body, CancellationToken cancellationToken)
    {
        var availableKeys = _apiKeys.Where(key => !string.IsNullOrWhiteSpace(key)).ToArray();
        if (availableKeys.Length == 0)
        {
            throw new InvalidOperationException("Gemini:ApiKey chưa được cấu hình.");
        }

        var startIndex = Math.Abs(Interlocked.Increment(ref _nextApiKeyIndex)) % availableKeys.Length;
        HttpResponseMessage? lastResponse = null;
        for (var attempt = 0; attempt < availableKeys.Length; attempt++)
        {
            var apiKey = availableKeys[(startIndex + attempt) % availableKeys.Length];
            var apiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3.6-flash:generateContent?key={apiKey}";
            try
            {
                var jsonContent = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(apiUrl, jsonContent, cancellationToken);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                var isQuotaError = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
                    responseBody.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
                    responseBody.Contains("resource exhausted", StringComparison.OrdinalIgnoreCase);

                if (!isQuotaError || attempt == availableKeys.Length - 1)
                {
                    return response;
                }

                lastResponse = response;
                response.Dispose();
            }
            catch (HttpRequestException) when (attempt < availableKeys.Length - 1)
            {
                continue;
            }
        }

        return lastResponse ?? throw new InvalidOperationException("Không thể gọi Gemini với các API key hiện có.");
    }

    private static JsonElement? TruncateChatHistory(JsonElement? result, int maxMessages)
    {
        if (result is not { ValueKind: JsonValueKind.Array } history)
        {
            return result;
        }

        return JsonSerializer.SerializeToElement(history.EnumerateArray().TakeLast(maxMessages).ToList());
    }

    private static string ExtractTextFromGeminiResponse(JsonElement root)
    {
        // 1. Kiểm tra xem Gemini có trả về lỗi không
        if (root.TryGetProperty("error", out var errorInfo))
        {
            var errorMsg = errorInfo.TryGetProperty("message", out var msg) ? msg.GetString() : "Lỗi không xác định";
            return $"Lỗi từ API Gemini: {errorMsg}";
        }

        // Nếu thành công thì mới bóc tách nội dung
        try
        {
            // 2. Bóc tách JSON an toàn bằng TryGetProperty
            if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
            {
                var firstCandidate = candidates[0];

                // Nếu AI bị chặn hoặc không có nội dung
                if (!firstCandidate.TryGetProperty("content", out var content))
                {
                    var finishReason = firstCandidate.TryGetProperty("finishReason", out var reason) ? reason.GetString() : "Không rõ lý do";
                    return $"AI không trả về nội dung (Lý do dừng: {finishReason}).";
                }

                if (content.TryGetProperty("parts", out var parts))
                {
                    // Lặp qua tất cả các parts để tìm văn bản (text)
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var textElem))
                        {
                            return textElem.GetString() ?? "";
                        }
                    }

                    // Trường hợp AI ngoan cố muốn gọi thêm một Tool nữa thay vì trả lời
                    if (parts.GetArrayLength() > 0 && parts[0].TryGetProperty("functionCall", out var funcCall))
                    {
                        var funcName = funcCall.TryGetProperty("name", out var nameElem) ? nameElem.GetString() : "Unknown";
                        return $"[Hệ thống] Dữ liệu đã được xử lý bằng hàm {funcName}, nhưng AI chưa đưa ra câu trả lời cuối cùng.";
                    }
                }
            }
            
            return "Không tìm thấy nội dung văn bản trong phản hồi của AI.";
        }
        catch (Exception ex)
        {
            return $"Lỗi parse JSON ở Backend: {ex.Message}";
        }
    }
}