import { DEMO_IMAGES } from './demoImages';

// A demo "kind" — what the active panel is demonstrating. Chat splits into text/code/reasoning
// (same transport, different prompts); vision/tools/audio are distinct request shapes.
export type Kind = 'text' | 'code' | 'reasoning' | 'vision' | 'tools' | 'audio';

export type ToolPreset = 'weather' | 'calculate' | 'both';

export type Scenario = {
  id: string;
  label: string;
  hint: string;
  prompt?: string;
  image?: string;              // vision: built-in image data URL
  audioUrl?: string;           // audio: built-in clip served from /public
  language?: string;           // audio: optional language hint (e.g. "en")
  upload?: boolean;            // vision/audio: this scenario expects a user upload
  tools?: ToolPreset;          // tools: which functions to expose
  toolChoice?: 'auto' | string; // tools: 'auto' or a forced function name
  followUpToolResult?: string; // tools: synthetic tool result to feed back (multi-turn loop)
};

// At least 5 interactive scenarios per capability so the selected model can be demoed thoroughly.

export const TEXT_SCENARIOS: Scenario[] = [
  { id: 'qa', label: 'Q&A', hint: 'Open-ended question answering',
    prompt: 'Explain why local, OpenAI-compatible LLM endpoints are useful for developers. Answer in 3 sentences.' },
  { id: 'summarize', label: 'Summarize', hint: 'Condense a document (à la tutorial-document-summarizer)',
    prompt: 'Summarize the following text in exactly 3 bullet points:\n\n"Foundry Local runs language models directly on your device. Your data never leaves the machine, responses start with no network latency, and there are no per-token costs or API keys. It exposes an OpenAI-compatible endpoint so existing tooling works unchanged."' },
  { id: 'translate', label: 'Translate', hint: 'Multi-language translation',
    prompt: 'Translate "Good morning! Welcome to the Foundry Local demo." into French, Spanish, and Japanese. Label each.' },
  { id: 'sentiment', label: 'Classify', hint: 'Sentiment classification',
    prompt: 'Classify the sentiment of each line as positive, negative, or neutral:\n1. I absolutely love this product.\n2. The wait was far too long.\n3. The package arrived on Tuesday.' },
  { id: 'extract', label: 'Extract JSON', hint: 'Structured data extraction',
    prompt: 'Extract the person, company, and amount as JSON from:\n"Maria from Contoso approved a $4,500 budget for Q3."' },
  { id: 'rewrite', label: 'Rewrite', hint: 'Tone / style rewriting',
    prompt: 'Rewrite this email to be polite and professional:\n"send me the report now, you are late."' },
];

export const CODE_SCENARIOS: Scenario[] = [
  { id: 'generate', label: 'Generate', hint: 'Write a function from a spec',
    prompt: 'Write a Python function is_prime(n) that returns True if n is prime. Include a short docstring.' },
  { id: 'explain', label: 'Explain', hint: 'Explain unfamiliar code',
    prompt: 'Explain what this code does, step by step:\n\ndef f(x):\n    return [i for i in range(1, x) if x % i == 0]' },
  { id: 'bug', label: 'Find the bug', hint: 'Debug and fix',
    prompt: 'Find and fix the bug in this function, and say what was wrong:\n\ndef average(nums):\n    return sum(nums) / len(nums) + 1' },
  { id: 'tests', label: 'Write tests', hint: 'Generate unit tests',
    prompt: 'Write pytest unit tests for a function add(a, b) that returns a + b. Cover positives, negatives, and zero.' },
  { id: 'translate', label: 'Port language', hint: 'Translate between languages',
    prompt: 'Translate this Python to idiomatic JavaScript:\n\ndef greet(name):\n    return f"Hello, {name}!"' },
];

export const REASONING_SCENARIOS: Scenario[] = [
  { id: 'math', label: 'Word problem', hint: 'Multi-step arithmetic',
    prompt: 'A train travels 240 km in 3 hours, then 180 km in 2 more hours. What is its average speed for the whole trip? Show your reasoning, then give the final number.' },
  { id: 'logic', label: 'Logic puzzle', hint: 'Deductive reasoning',
    prompt: 'Ann, Bob, and Cara each own a different pet: cat, dog, or fish. Ann does not own the cat. Bob owns the dog. Who owns the fish? Explain your reasoning.' },
  { id: 'plan', label: 'Plan', hint: 'Step-by-step planning',
    prompt: 'I have 2 hours to prepare a 10-minute product demo. Give me a concrete step-by-step plan with time budgets.' },
  { id: 'compare', label: 'Compare', hint: 'Weigh trade-offs',
    prompt: 'Compare SQLite and PostgreSQL for a small single-user desktop app. Give 3 pros and 3 cons each, then recommend one.' },
  { id: 'estimate', label: 'Estimate', hint: 'Fermi estimation',
    prompt: 'Estimate how many gas stations there are in the United States. State your assumptions and show the math.' },
];

export const VISION_SCENARIOS: Scenario[] = [
  { id: 'describe', label: 'Describe', hint: 'Describe a scene', image: DEMO_IMAGES.scene,
    prompt: 'Describe this image in one or two sentences.' },
  { id: 'ocr', label: 'Read text (OCR)', hint: 'Transcribe text in an image', image: DEMO_IMAGES.ocr,
    prompt: 'What text appears in this image? Transcribe it exactly.' },
  { id: 'color', label: 'Shape & color', hint: 'Identify shape and color', image: DEMO_IMAGES.shapes,
    prompt: 'What shape is shown and what color is it? Answer in one short sentence.' },
  { id: 'count', label: 'Count', hint: 'Count objects', image: DEMO_IMAGES.count,
    prompt: 'How many circles are in this image? Answer with just the number.' },
  { id: 'sceneqa', label: 'Visual Q&A', hint: 'Answer a yes/no question', image: DEMO_IMAGES.scene,
    prompt: 'Is there a sun in this picture? Answer yes or no, then explain briefly.' },
  { id: 'upload', label: 'Upload your own', hint: 'Bring your own image', upload: true,
    prompt: 'Describe this image in detail.' },
];

export const TOOLS_SCENARIOS: Scenario[] = [
  { id: 'weather', label: 'Weather', hint: 'get_weather(city)', tools: 'weather', toolChoice: 'auto',
    prompt: "What's the weather in Paris right now?" },
  { id: 'calculate', label: 'Calculator', hint: 'calculate(expression)', tools: 'calculate', toolChoice: 'auto',
    prompt: 'What is 1234 multiplied by 56?' },
  { id: 'both', label: 'Two tools', hint: 'Model picks the right tool', tools: 'both', toolChoice: 'auto',
    prompt: "What's the weather in Tokyo, and separately, what is 15% of 200?" },
  { id: 'forced', label: 'Forced tool', hint: 'tool_choice forces get_weather', tools: 'weather', toolChoice: 'get_weather',
    prompt: 'Tell me about the city of London.' },
  { id: 'multiturn', label: 'Full tool loop', hint: 'Feed a tool result back for a final answer', tools: 'weather', toolChoice: 'auto',
    followUpToolResult: '{"temp_c":8,"condition":"light rain"}',
    prompt: 'What should I wear in Berlin today? Use the weather tool, then advise.' },
];

export const AUDIO_SCENARIOS: Scenario[] = [
  { id: 'pangram', label: 'Pangram clip', hint: 'Built-in: "the quick brown fox…"', audioUrl: '/sample-pangram.wav' },
  { id: 'numbers', label: 'Numbers clip', hint: 'Built-in: dates & counts', audioUrl: '/sample-numbers.wav' },
  { id: 'pangram-en', label: 'Pangram (lang=en)', hint: 'Language hint forces English', audioUrl: '/sample-pangram.wav', language: 'en' },
  { id: 'numbers-en', label: 'Numbers (lang=en)', hint: 'Language hint forces English', audioUrl: '/sample-numbers.wav', language: 'en' },
  { id: 'upload', label: 'Upload your own', hint: 'Bring your own audio file', upload: true },
];

export function scenariosFor(kind: Kind): Scenario[] {
  switch (kind) {
    case 'text': return TEXT_SCENARIOS;
    case 'code': return CODE_SCENARIOS;
    case 'reasoning': return REASONING_SCENARIOS;
    case 'vision': return VISION_SCENARIOS;
    case 'tools': return TOOLS_SCENARIOS;
    case 'audio': return AUDIO_SCENARIOS;
  }
}
