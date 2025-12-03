var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapPost("/", async (context) =>
{
    try
    {
        using var reader = new StreamReader(context.Request.Body);
        var sourceCode = await reader.ReadToEndAsync();
        var psCode = CsPsc.Compiler.Run(sourceCode);
        context.Response.ContentType = "application/postscript";
        await context.Response.WriteAsync(psCode);
    }
    catch (Exception ex)
    {
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync($"Error: {ex.Message}");
    }
});

app.Run("http://*:8081");
