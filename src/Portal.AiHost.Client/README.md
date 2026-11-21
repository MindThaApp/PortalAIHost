# Portal.AiHost.Client

.NET client for Portal AI Host, the local AI service shared by the Portal apps.

- Finds the host (environment override, discovery file, default port) and validates it.
- Gets user-approved access once, and keeps the token encrypted with DPAPI.
- Calls chat (including streaming), embeddings, models, status and capabilities.
- Retries the transient errors the API marks as retryable, within your deadline.
- Calls a local model of your choice when the host is unavailable, and tells you so.

```csharp
var client = AiHostClient.Create(new AiHostClientOptions
{
    AppId = "com.example.myapp",
    AppDisplayName = "My App",
    Fallback = new MyLocalModel(),
});
client.FallbackActivated += (_, e) => ShowBanner(e.Message);

// From a user action such as switching on "Use Portal AI Host":
await client.RequestAccessAsync(wait: true);

await foreach (var delta in client.StreamChatAsync(request))
    Console.Write(delta.Text);
```

The package is trimming and AOT friendly and carries the `Portal.AiHost.Contracts` types. Any OpenAI SDK also
works: use `BaseAddress` and `GetBearerToken()`.

Licence: MIT.
