using Microsoft.Extensions.Logging;
using Ashlar.Abstractions;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Orchestration.Architect.Models;
using System.Text.Json;
using ModelInput = Ashlar.Abstractions.ModelInput;

namespace Ashlar.Orchestration.Agents.Planning;

/// <summary>
/// Agent that guides users through the iterative game development process by asking questions.
/// 
/// Responsibilities:
/// - Presents structured questions to gather requirements
/// - Collects user responses iteratively
/// - Generates recommendations based on responses
/// - Provides planning guidance for game development
/// 
/// Uses a question-based workflow to help users define their game development needs.
/// Inherits from BaseDomainAgent for LLM integration.
/// </summary>
public class PlanningAgent : BaseDomainAgent
{
    private readonly List<PlanningQuestion> _questions = new();
    private int _currentQuestionIndex = 0;
    private readonly Dictionary<string, string> _userResponses = new();

    public PlanningAgent(
        AgentSpawnSpec spec,
        ILogger<BaseAgent> logger,
        IModel? model = null)
        : base(spec, logger, model)
    {
        InitializeQuestions();
    }

    protected override Task OnInitializeAsync(CancellationToken cancellationToken)
    {
        Logger.LogInformation("Initializing Planning Agent: {AgentId}", Spec.AgentId);
        return Task.CompletedTask;
    }

    protected override Task OnDependenciesResolvedAsync(
        IReadOnlyDictionary<string, object> dependencyOutputs,
        CancellationToken cancellationToken)
    {
        Logger.LogDebug("Dependencies resolved for {AgentId}", Spec.AgentId);
        return Task.CompletedTask;
    }

    protected override async Task<object> OnExecuteAsync(
        IReadOnlyDictionary<string, object>? dependencyOutputs,
        CancellationToken cancellationToken)
    {
        // Get user response from dependencies if available
        var userResponse = dependencyOutputs?.GetValueOrDefault("userResponse")?.ToString();
        var questionId = dependencyOutputs?.GetValueOrDefault("questionId")?.ToString();

        if (!string.IsNullOrEmpty(questionId) && !string.IsNullOrEmpty(userResponse))
        {
            _userResponses[questionId] = userResponse;
            _currentQuestionIndex++;
        }

        // Get next question or generate recommendation
        if (_currentQuestionIndex < _questions.Count)
        {
            var question = _questions[_currentQuestionIndex];
            return new PlanningResponse
            {
                Type = PlanningResponseType.Question,
                Question = question,
                Progress = (double)_currentQuestionIndex / _questions.Count,
                Context = GatherContext()
            };
        }
        else
        {
            // All questions answered, generate recommendation
            return await GenerateRecommendation(cancellationToken);
        }
    }

    protected override Task OnShutdownAsync(CancellationToken cancellationToken)
    {
        Logger.LogInformation("Shutting down Planning Agent: {AgentId}", Spec.AgentId);
        return Task.CompletedTask;
    }

    protected override string GetSystemPrompt()
    {
        return @"You are a helpful game development planning assistant. Your role is to guide users through the iterative game development process by asking thoughtful questions and providing recommendations based on their responses. Be friendly, encouraging, and focus on helping users create games they're excited about.";
    }

    protected override object GetMockOutput()
    {
        if (_currentQuestionIndex < _questions.Count)
        {
            return new PlanningResponse
            {
                Type = PlanningResponseType.Question,
                Question = _questions[_currentQuestionIndex],
                Progress = (double)_currentQuestionIndex / _questions.Count,
                Context = GatherContext()
            };
        }
        return new PlanningResponse
        {
            Type = PlanningResponseType.Recommendation,
            Recommendation = "Based on your responses, I recommend starting with a simple action game. You can iterate and improve it based on playtest feedback.",
            Progress = 1.0,
            Context = GatherContext()
        };
    }

    private void InitializeQuestions()
    {
        _questions.Add(new PlanningQuestion
        {
            Id = "genre",
            Text = "What genre of game would you like to create? (e.g., Action, RPG, Puzzle, Strategy, Platformer)",
            Category = "Core",
            Options = new[] { "Action", "RPG", "Puzzle", "Strategy", "Platformer", "Other" }
        });

        _questions.Add(new PlanningQuestion
        {
            Id = "scope",
            Text = "What's the scope of your game?",
            Category = "Scope",
            Options = new[] { "Small (prototype)", "Medium (indie game)", "Large (full game)" }
        });

        _questions.Add(new PlanningQuestion
        {
            Id = "target_audience",
            Text = "Who is your target audience?",
            Category = "Audience",
            Options = new[] { "Casual players", "Hardcore gamers", "Children", "Everyone" }
        });

        _questions.Add(new PlanningQuestion
        {
            Id = "art_style",
            Text = "What art style do you prefer?",
            Category = "Art",
            Options = new[] { "Realistic", "Stylized", "Pixel art", "Low poly", "Minimalist" }
        });

        _questions.Add(new PlanningQuestion
        {
            Id = "core_mechanic",
            Text = "What's the core gameplay mechanic you want to focus on?",
            Category = "Gameplay",
            Options = new[] { "Combat", "Exploration", "Puzzle solving", "Building", "Social interaction" }
        });

        _questions.Add(new PlanningQuestion
        {
            Id = "platform",
            Text = "What platform(s) are you targeting?",
            Category = "Platform",
            Options = new[] { "PC", "Mobile", "Console", "Web", "Multiple" }
        });
    }

    private async Task<PlanningResponse> GenerateRecommendation(CancellationToken cancellationToken)
    {
        var context = GatherContext();
        
        if (Model != null)
        {
            try
            {
                var prompt = $@"Based on the user's responses, provide a game development recommendation:

{string.Join("\n", _userResponses.Select(kvp => $"{kvp.Key}: {kvp.Value}"))}

Provide:
1. A brief game concept summary
2. Recommended starting features
3. Suggested iteration priorities
4. Any warnings or considerations

Format as JSON:
{{
  ""summary"": ""..."",
  ""recommendedFeatures"": [""..."", ""...""],
  ""iterationPriorities"": [""..."", ""...""],
  ""considerations"": [""..."", ""...""]
}}";

                var modelInput = new ModelInput(new[]
                {
                    ("system", GetSystemPrompt()),
                    ("user", prompt)
                });

                var modelOutput = await Model.CompleteAsync(modelInput, cancellationToken);
                
                // Try to parse JSON response
                try
                {
                    var jsonDoc = JsonDocument.Parse(modelOutput.Text);
                    var root = jsonDoc.RootElement;
                    
                    return new PlanningResponse
                    {
                        Type = PlanningResponseType.Recommendation,
                        Recommendation = root.TryGetProperty("summary", out var summary) 
                            ? summary.GetString() ?? "Ready to start development!"
                            : "Ready to start development!",
                        RecommendedFeatures = root.TryGetProperty("recommendedFeatures", out var features)
                            ? features.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList()
                            : new List<string>(),
                        IterationPriorities = root.TryGetProperty("iterationPriorities", out var priorities)
                            ? priorities.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList()
                            : new List<string>(),
                        Considerations = root.TryGetProperty("considerations", out var considerations)
                            ? considerations.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList()
                            : new List<string>(),
                        Progress = 1.0,
                        Context = context
                    };
                }
                catch
                {
                    // If JSON parsing fails, use raw text
                    return new PlanningResponse
                    {
                        Type = PlanningResponseType.Recommendation,
                        Recommendation = modelOutput.Text,
                        Progress = 1.0,
                        Context = context
                    };
                }
            }
            catch (Exception ex)
            {
                // A refusal is the final error, never a reason for the canned recommendation (SPEC-007 D23, 4.8b).
                EgressRefusal.ThrowIfPresent(ex);
                Logger.LogWarning(ex, "Failed to generate recommendation with LLM");
            }
        }

        // Fallback recommendation
        return new PlanningResponse
        {
            Type = PlanningResponseType.Recommendation,
            Recommendation = GenerateFallbackRecommendation(),
            Progress = 1.0,
            Context = context
        };
    }

    private string GenerateFallbackRecommendation()
    {
        var genre = _userResponses.GetValueOrDefault("genre", "Action");
        var scope = _userResponses.GetValueOrDefault("scope", "Medium");
        
        return $"Based on your preferences for a {scope.ToLower()} {genre} game, I recommend starting with core gameplay mechanics and iterating based on playtest feedback. Focus on making the core loop fun first, then expand from there.";
    }

    private Dictionary<string, object> GatherContext()
    {
        return new Dictionary<string, object>
        {
            ["responses"] = _userResponses,
            ["questionCount"] = _questions.Count,
            ["answeredCount"] = _currentQuestionIndex,
            ["progress"] = _currentQuestionIndex / (double)_questions.Count
        };
    }
}
