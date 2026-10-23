using DocIntake.Api.Services;

namespace DocIntake.Api.Endpoints;

public static class AskEndpoints
{
    public sealed record AskRequest(string Question);

    public static void MapAskEndpoints(this WebApplication app) =>
        app.MapPost("/ask", AskAsync).RequireAuthorization();

    private static async Task<IResult> AskAsync(
        AskRequest request,
        HttpContext context,
        IRagService rag,
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
        {
            return Results.BadRequest(new { error = "Question cannot be empty." });
        }

        if (!DocumentOwner.TryGet(context.User, environment, configuration, out var ownerId))
        {
            return Results.Forbid();
        }

        if (!configuration.GetValue("OpenAI:ChatEnabled", true))
        {
            logger.LogWarning("Question rejected because chat is disabled by configuration");
            return Results.Json(
                new { error = "Chat is disabled for this environment." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        logger.LogInformation("Received question (length {QuestionLength})", request.Question.Length);

        try
        {
            var response = await rag.AskAsync(request.Question, ownerId, cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                logger.LogInformation("No relevant content found to answer the question");
                return Results.NotFound(new { error = "No relevant content found to answer your question." });
            }

            logger.LogInformation("Answer generated successfully");
            return Results.Ok(response);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Request cancelled during processing");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing question");
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
    }
}
