namespace FoundryLocalLlmServer.BlazorClient;

// C# mirror of the TypeScript app's scenarios.ts — identical scenario presets so both clients demo
// the same things against the one Foundry Local server.

public sealed record Scenario(
    string Id,
    string Label,
    string Hint,
    string? Prompt = null,
    string? Image = null,
    string? AudioUrl = null,
    string? Language = null,
    bool Upload = false,
    string? Tools = null,          // "weather" | "calculate" | "both"
    string? ToolChoice = null,     // "auto" | function name
    string? FollowUpToolResult = null,
    string[]? Turns = null);

public static class Scenarios
{
    public static readonly Scenario[] Text =
    [
        new("qa", "Q&A", "Open-ended question answering",
            Prompt: "Explain why local, OpenAI-compatible LLM endpoints are useful for developers. Answer in 3 sentences."),
        new("summarize", "Summarize", "Condense a document",
            Prompt: "Summarize the following text in exactly 3 bullet points:\n\n\"Foundry Local runs language models directly on your device. Your data never leaves the machine, responses start with no network latency, and there are no per-token costs or API keys. It exposes an OpenAI-compatible endpoint so existing tooling works unchanged.\""),
        new("translate", "Translate", "Multi-language translation",
            Prompt: "Translate \"Good morning! Welcome to the Foundry Local demo.\" into French, Spanish, and Japanese. Label each."),
        new("sentiment", "Classify", "Sentiment classification",
            Prompt: "Classify the sentiment of each line as positive, negative, or neutral:\n1. I absolutely love this product.\n2. The wait was far too long.\n3. The package arrived on Tuesday."),
        new("extract", "Extract JSON", "Structured data extraction",
            Prompt: "Extract the person, company, and amount as JSON from:\n\"Maria from Contoso approved a $4,500 budget for Q3.\""),
        new("rewrite", "Rewrite", "Tone / style rewriting",
            Prompt: "Rewrite this email to be polite and professional:\n\"send me the report now, you are late.\""),
        new("conversation", "Multi-turn chat", "Context memory across a 3-turn conversation",
            Turns: ["My name is Alice and I have a golden retriever named Max.", "What kind of pet do I have, and what is its name?", "Remind me — what did I say my name was?"]),
    ];

    public static readonly Scenario[] Code =
    [
        new("generate", "Generate", "Write a function from a spec",
            Prompt: "Write a Python function is_prime(n) that returns True if n is prime. Include a short docstring."),
        new("explain", "Explain", "Explain unfamiliar code",
            Prompt: "Explain what this code does, step by step:\n\ndef f(x):\n    return [i for i in range(1, x) if x % i == 0]"),
        new("bug", "Find the bug", "Debug and fix",
            Prompt: "Find and fix the bug in this function, and say what was wrong:\n\ndef average(nums):\n    return sum(nums) / len(nums) + 1"),
        new("tests", "Write tests", "Generate unit tests",
            Prompt: "Write pytest unit tests for a function add(a, b) that returns a + b. Cover positives, negatives, and zero."),
        new("translate", "Port language", "Translate between languages",
            Prompt: "Translate this Python to idiomatic JavaScript:\n\ndef greet(name):\n    return f\"Hello, {name}!\""),
        new("iterate", "Iterative coding", "Refine code across a 3-turn conversation",
            Turns: ["Write a Python function add(a, b) that returns their sum.", "Now add type hints to it.", "Now add a docstring and one usage example."]),
    ];

    public static readonly Scenario[] Reasoning =
    [
        new("math", "Word problem", "Multi-step arithmetic",
            Prompt: "A train travels 240 km in 3 hours, then 180 km in 2 more hours. What is its average speed for the whole trip? Show your reasoning, then give the final number."),
        new("logic", "Logic puzzle", "Deductive reasoning",
            Prompt: "Ann, Bob, and Cara each own a different pet: cat, dog, or fish. Ann does not own the cat. Bob owns the dog. Who owns the fish? Explain your reasoning."),
        new("plan", "Plan", "Step-by-step planning",
            Prompt: "I have 2 hours to prepare a 10-minute product demo. Give me a concrete step-by-step plan with time budgets."),
        new("compare", "Compare", "Weigh trade-offs",
            Prompt: "Compare SQLite and PostgreSQL for a small single-user desktop app. Give 3 pros and 3 cons each, then recommend one."),
        new("estimate", "Estimate", "Fermi estimation",
            Prompt: "Estimate how many gas stations there are in the United States. State your assumptions and show the math."),
        new("followup", "Follow-up reasoning", "Build on the previous answer across turns",
            Turns: ["A shop sells apples at $2 each. How much do 5 apples cost?", "And if I buy 12 apples instead?", "Now apply a 10% discount to that 12-apple total. What is the final price?"]),
    ];

    public static readonly Scenario[] Vision =
    [
        new("describe", "Describe", "Describe a scene", Image: DemoImages.All["scene"], Prompt: "Describe this image in one or two sentences."),
        new("ocr", "Read text (OCR)", "Transcribe text in an image", Image: DemoImages.All["ocr"], Prompt: "What text appears in this image? Transcribe it exactly."),
        new("color", "Shape & color", "Identify shape and color", Image: DemoImages.All["shapes"], Prompt: "What shape is shown and what color is it? Answer in one short sentence."),
        new("count", "Count", "Count objects", Image: DemoImages.All["count"], Prompt: "How many circles are in this image? Answer with just the number."),
        new("sceneqa", "Visual Q&A", "Answer a yes/no question", Image: DemoImages.All["scene"], Prompt: "Is there a sun in this picture? Answer yes or no, then explain briefly."),
        new("upload", "Upload your own", "Bring your own image", Upload: true, Prompt: "Describe this image in detail."),
    ];

    public static readonly Scenario[] Tools =
    [
        new("weather", "Weather", "get_weather(city)", Tools: "weather", ToolChoice: "auto", Prompt: "What's the weather in Paris right now?"),
        new("calculate", "Calculator", "calculate(expression)", Tools: "calculate", ToolChoice: "auto", Prompt: "What is 1234 multiplied by 56?"),
        new("both", "Two tools", "Model picks the right tool", Tools: "both", ToolChoice: "auto", Prompt: "What's the weather in Tokyo, and separately, what is 15% of 200?"),
        new("forced", "Forced tool", "tool_choice forces get_weather", Tools: "weather", ToolChoice: "get_weather", Prompt: "Tell me about the city of London."),
        new("multiturn", "Full tool loop", "Feed a tool result back for a final answer", Tools: "weather", ToolChoice: "auto", FollowUpToolResult: "{\"temp_c\":8,\"condition\":\"light rain\"}", Prompt: "What should I wear in Berlin today? Use the weather tool, then advise."),
    ];

    public static readonly Scenario[] Audio =
    [
        new("pangram", "Pangram clip", "Built-in: \"the quick brown fox…\"", AudioUrl: "sample-pangram.wav"),
        new("numbers", "Numbers clip", "Built-in: dates & counts", AudioUrl: "sample-numbers.wav"),
        new("pangram-en", "Pangram (lang=en)", "Language hint forces English", AudioUrl: "sample-pangram.wav", Language: "en"),
        new("numbers-en", "Numbers (lang=en)", "Language hint forces English", AudioUrl: "sample-numbers.wav", Language: "en"),
        new("upload", "Upload your own", "Bring your own audio file", Upload: true),
    ];

    public static Scenario[] For(string kind) => kind switch
    {
        "text" => Text,
        "code" => Code,
        "reasoning" => Reasoning,
        "vision" => Vision,
        "tools" => Tools,
        "audio" => Audio,
        _ => Text,
    };
}
