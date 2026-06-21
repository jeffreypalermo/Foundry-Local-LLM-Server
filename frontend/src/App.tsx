import { useEffect, useMemo, useState } from 'react';
import type { ChangeEvent, FormEvent } from 'react';
import './App.css';
import { scenariosFor } from './scenarios';
import type { Kind, Scenario, ToolPreset } from './scenarios';

type FoundryConfig = { endpoint: string; model: string };

type ModelInfo = {
  id: string;
  capabilities: string[];
  text: boolean;
  code: boolean;
  reasoning: boolean;
  vision: boolean;
  audio: boolean;
  tools: boolean;
};

type ModelsResponse = { current: string; available: ModelInfo[] };

type ChatTurn = { role: 'user' | 'assistant'; content: string };

type Mode = 'chat' | 'vision' | 'tools' | 'audio';

function modesFor(model: ModelInfo | null): Mode[] {
  if (!model) return ['chat'];
  const modes: Mode[] = [];
  if (model.text) modes.push('chat');
  if (model.vision) modes.push('vision');
  if (model.tools) modes.push('tools');
  if (model.audio) modes.push('audio');
  return modes.length > 0 ? modes : ['chat'];
}

const MODE_LABELS: Record<Mode, string> = {
  chat: 'Text chat',
  vision: 'Vision',
  tools: 'Tools',
  audio: 'Speech-to-text',
};

function kindFor(mode: Mode, model: ModelInfo | null): Kind {
  if (mode === 'vision') return 'vision';
  if (mode === 'tools') return 'tools';
  if (mode === 'audio') return 'audio';
  if (model?.code) return 'code';
  if (model?.reasoning) return 'reasoning';
  return 'text';
}

function App() {
  const [config, setConfig] = useState<FoundryConfig | null>(null);
  const [models, setModels] = useState<ModelInfo[]>([]);
  const [selected, setSelected] = useState<string>('');
  const [mode, setMode] = useState<Mode>('chat');

  const [scenario, setScenario] = useState<Scenario | null>(null);
  const [prompt, setPrompt] = useState('');
  const [chat, setChat] = useState<ChatTurn[]>([]);
  const [imageDataUrl, setImageDataUrl] = useState<string | null>(null);
  const [audioSampleUrl, setAudioSampleUrl] = useState<string | null>(null);
  const [audioLanguage, setAudioLanguage] = useState<string | null>(null);
  const [audioFile, setAudioFile] = useState<File | null>(null);
  const [transcript, setTranscript] = useState<string | null>(null);

  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const currentModel = useMemo(() => models.find((m) => m.id === selected) ?? null, [models, selected]);
  const availableModes = useMemo(() => modesFor(currentModel), [currentModel]);
  const kind = useMemo(() => kindFor(mode, currentModel), [mode, currentModel]);
  const scenarios = useMemo(() => scenariosFor(kind), [kind]);

  // Load config + model catalog on startup.
  useEffect(() => {
    void (async () => {
      try {
        const [cfgRes, modelsRes] = await Promise.all([fetch('/api/foundry'), fetch('/api/models')]);
        if (!cfgRes.ok) throw new Error(`Could not load Foundry settings (${cfgRes.status})`);
        if (!modelsRes.ok) throw new Error(`Could not load model catalog (${modelsRes.status})`);
        setConfig((await cfgRes.json()) as FoundryConfig);
        const list = (await modelsRes.json()) as ModelsResponse;
        setModels(list.available);
        setSelected(list.current);
      } catch (err: unknown) {
        setError(err instanceof Error ? err.message : 'Unknown startup error');
      }
    })();
  }, []);

  // Keep the active mode valid for the selected model.
  useEffect(() => {
    if (!availableModes.includes(mode)) setMode(availableModes[0]);
  }, [availableModes, mode]);

  // When the demo kind changes (mode/model switch), load that kind's first scenario.
  useEffect(() => {
    applyScenario(scenarios[0]);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [kind]);

  function applyScenario(s: Scenario | undefined) {
    if (!s) return;
    setScenario(s);
    setChat([]);
    setTranscript(null);
    setError(null);
    if (s.prompt !== undefined) setPrompt(s.prompt);
    setImageDataUrl(s.image ?? null);
    setAudioSampleUrl(s.audioUrl ?? null);
    setAudioLanguage(s.language ?? null);
    setAudioFile(null);
  }

  const onSelectModel = async (event: ChangeEvent<HTMLSelectElement>) => {
    const model = event.target.value;
    setSelected(model);
    // Clear the previous model's conversation/result so stale output isn't attributed to the new
    // model. (The scenario-reset effect only fires when the demo *kind* changes, so same-kind
    // switches — e.g. text→text — would otherwise leave the old transcript on screen.)
    setChat([]);
    setTranscript(null);
    setError(null);
    try {
      const res = await fetch('/api/models/select', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ model }),
      });
      if (!res.ok) throw new Error(`Could not select model (${res.status})`);
      setConfig((c) => (c ? { ...c, model } : c));
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Could not select model');
    }
  };

  // ── Senders ──────────────────────────────────────────────────────────────────
  async function postChat(messages: unknown[], extra: Record<string, unknown> = {}) {
    const res = await fetch('/v1/chat/completions', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ model: selected, messages, ...extra }),
    });
    if (!res.ok) throw new Error(await readError(res));
    return (await res.json()) as ChatCompletion;
  }

  const sendChat = async (event: FormEvent) => {
    event.preventDefault();
    if (!prompt.trim() || busy) return;
    setBusy(true);
    setError(null);
    const next: ChatTurn[] = [...chat, { role: 'user', content: prompt.trim() }];
    setChat(next);
    try {
      const payload = await postChat(next);
      setChat([...next, { role: 'assistant', content: contentOf(payload) }]);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Completion error');
      setChat(next);
    } finally {
      setBusy(false);
    }
  };

  const onPickImage = (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    if (!file) return;
    const reader = new FileReader();
    reader.onload = () => setImageDataUrl(reader.result as string);
    reader.readAsDataURL(file);
  };

  const sendVision = async (event: FormEvent) => {
    event.preventDefault();
    if (!imageDataUrl || !prompt.trim() || busy) return;
    setBusy(true);
    setError(null);
    const next: ChatTurn[] = [...chat, { role: 'user', content: prompt.trim() }];
    setChat(next);
    try {
      const payload = await postChat([
        {
          role: 'user',
          content: [
            { type: 'text', text: prompt.trim() },
            { type: 'image_url', image_url: { url: imageDataUrl } },
          ],
        },
      ]);
      setChat([...next, { role: 'assistant', content: contentOf(payload) }]);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Vision request error');
      setChat(next);
    } finally {
      setBusy(false);
    }
  };

  const sendTools = async (event: FormEvent) => {
    event.preventDefault();
    if (!prompt.trim() || busy || !scenario) return;
    setBusy(true);
    setError(null);
    const userTurn: ChatTurn = { role: 'user', content: prompt.trim() };
    setChat([userTurn]);
    try {
      const tools = toolsFor(scenario.tools ?? 'weather');
      const toolChoice = toolChoiceArg(scenario.toolChoice);
      const first = await postChat([userTurn], { tools, tool_choice: toolChoice });
      const msg = first.choices?.[0]?.message;
      const calls = msg?.tool_calls ?? [];

      if (calls.length > 0) {
        const callLines = calls.map((c) => `🛠 ${c.function?.name}(${c.function?.arguments})`).join('\n');
        // Multi-turn loop: feed a synthetic tool result back for a final answer.
        if (scenario.followUpToolResult) {
          const assistantMsg = { role: 'assistant', content: msg?.content ?? '', tool_calls: calls };
          const toolMsgs = calls.map((c) => ({
            role: 'tool',
            tool_call_id: c.id ?? c.function?.name ?? 'call_0',
            content: scenario.followUpToolResult,
          }));
          const second = await postChat([userTurn, assistantMsg, ...toolMsgs], { tools });
          setChat([
            userTurn,
            { role: 'assistant', content: `${callLines}\n↩ tool result: ${scenario.followUpToolResult}\n\n${contentOf(second)}` },
          ]);
        } else {
          setChat([userTurn, { role: 'assistant', content: callLines }]);
        }
      } else {
        setChat([userTurn, { role: 'assistant', content: msg?.content ?? 'No content returned.' }]);
      }
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Tool request error');
    } finally {
      setBusy(false);
    }
  };

  const sendAudio = async (event: FormEvent) => {
    event.preventDefault();
    if (busy) return;
    if (!audioFile && !audioSampleUrl) return;
    setBusy(true);
    setError(null);
    setTranscript(null);
    try {
      let blob: Blob;
      let filename: string;
      if (audioFile) {
        blob = audioFile;
        filename = audioFile.name;
      } else {
        const r = await fetch(audioSampleUrl!);
        if (!r.ok) throw new Error(`Could not load sample clip (${r.status})`);
        blob = await r.blob();
        filename = audioSampleUrl!.split('/').pop() ?? 'audio.wav';
      }
      const form = new FormData();
      form.append('model', selected);
      form.append('file', blob, filename);
      if (audioLanguage) form.append('language', audioLanguage);
      const res = await fetch('/v1/audio/transcriptions', { method: 'POST', body: form });
      if (!res.ok) throw new Error(await readError(res));
      const payload = (await res.json()) as { text?: string };
      setTranscript(payload.text ?? '(empty transcript)');
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Transcription error');
    } finally {
      setBusy(false);
    }
  };

  return (
    <main className="app-shell">
      <h1>Foundry Local OpenAI Server</h1>

      <div className="controls">
        <p className="config-line">
          Model: <strong>{config?.model ?? selected ?? 'loading...'}</strong>
        </p>
        <label className="picker">
          Switch model:{' '}
          <select value={selected} onChange={onSelectModel} disabled={busy || models.length === 0} aria-label="Model">
            {models.map((m) => (
              <option key={m.id} value={m.id}>
                {m.id} — {m.capabilities.join(', ')}
              </option>
            ))}
          </select>
        </label>
        <p className="config-line">
          Foundry endpoint: <code>{config?.endpoint ?? 'loading...'}</code>
        </p>
        {currentModel && (
          <p className="config-line caps">
            Capabilities:{' '}
            {currentModel.capabilities.map((c) => (
              <span key={c} className="cap-badge">{c}</span>
            ))}
          </p>
        )}
      </div>

      {availableModes.length > 1 && (
        <nav className="mode-tabs" aria-label="Capability">
          {availableModes.map((m) => (
            <button
              key={m}
              type="button"
              className={`mode-tab ${m === mode ? 'active' : ''}`}
              onClick={() => setMode(m)}
              disabled={busy}
            >
              {MODE_LABELS[m]}
            </button>
          ))}
        </nav>
      )}

      <section className="scenarios" aria-label="Demo scenarios">
        <p className="scenarios-title">Demo scenarios — {kind}:</p>
        <div className="scenario-chips">
          {scenarios.map((s) => (
            <button
              key={s.id}
              type="button"
              data-testid={`scenario-${s.id}`}
              className={`scenario-chip ${scenario?.id === s.id ? 'active' : ''}`}
              onClick={() => applyScenario(s)}
              disabled={busy}
              title={s.hint}
            >
              {s.label}
            </button>
          ))}
        </div>
        {scenario && <p className="scenario-hint">{scenario.hint}</p>}
      </section>

      {error && <p className="error">{error}</p>}

      {mode === 'chat' && (
        <section className="panel" aria-label="Text chat">
          <form onSubmit={sendChat} className="prompt-form">
            <textarea value={prompt} onChange={(e) => setPrompt(e.target.value)} placeholder="Enter a prompt" rows={5} disabled={busy} />
            <button type="submit" disabled={busy || !config}>{busy ? 'Running...' : 'Send Prompt'}</button>
          </form>
          <ChatLog chat={chat} />
        </section>
      )}

      {mode === 'vision' && (
        <section className="panel" aria-label="Vision">
          <form onSubmit={sendVision} className="prompt-form">
            <input type="file" accept="image/*" onChange={onPickImage} disabled={busy} aria-label="Image" />
            {imageDataUrl && <img className="preview" src={imageDataUrl} alt="selected" />}
            <textarea value={prompt} onChange={(e) => setPrompt(e.target.value)} placeholder="Ask about the image" rows={3} disabled={busy} />
            <button type="submit" disabled={busy || !imageDataUrl}>{busy ? 'Running...' : 'Describe Image'}</button>
          </form>
          <ChatLog chat={chat} />
        </section>
      )}

      {mode === 'tools' && (
        <section className="panel" aria-label="Tools">
          <p className="hint">Tools available: <code>get_weather(city)</code>, <code>calculate(expression)</code>. The model may answer in text or emit tool calls.</p>
          <form onSubmit={sendTools} className="prompt-form">
            <textarea value={prompt} onChange={(e) => setPrompt(e.target.value)} placeholder="e.g. What's the weather in Paris?" rows={3} disabled={busy} />
            <button type="submit" disabled={busy}>{busy ? 'Running...' : 'Send with Tools'}</button>
          </form>
          <ChatLog chat={chat} />
        </section>
      )}

      {mode === 'audio' && (
        <section className="panel" aria-label="Speech-to-text">
          <form onSubmit={sendAudio} className="prompt-form">
            <p className="hint">
              {audioFile ? `File: ${audioFile.name}` : audioSampleUrl ? `Built-in clip: ${audioSampleUrl}` : 'Choose a built-in clip or upload audio.'}
              {audioLanguage ? ` · language=${audioLanguage}` : ''}
            </p>
            <input type="file" accept="audio/*,.wav,.mp3,.m4a,.flac,.ogg" onChange={(e) => setAudioFile(e.target.files?.[0] ?? null)} disabled={busy} aria-label="Audio file" />
            <button type="submit" disabled={busy || (!audioFile && !audioSampleUrl)}>{busy ? 'Transcribing...' : 'Transcribe'}</button>
          </form>
          {transcript !== null && (
            <article className="message assistant">
              <h2>Transcript</h2>
              <p>{transcript}</p>
            </article>
          )}
        </section>
      )}
    </main>
  );
}

function ChatLog({ chat }: { chat: ChatTurn[] }) {
  return (
    <section className="chat-log" aria-label="Chat transcript">
      {chat.map((entry, index) => (
        <article key={index} className={`message ${entry.role}`}>
          <h2>{entry.role === 'user' ? 'User' : 'Assistant'}</h2>
          <p>{entry.content}</p>
        </article>
      ))}
    </section>
  );
}

// ── Helpers / types ───────────────────────────────────────────────────────────────

type ToolCall = { id?: string; function?: { name?: string; arguments?: string } };
type ChatCompletion = {
  choices?: Array<{ message?: { content?: string; tool_calls?: ToolCall[] } }>;
};

const WEATHER_TOOL = {
  type: 'function',
  function: {
    name: 'get_weather',
    description: 'Get the current weather for a city',
    parameters: { type: 'object', properties: { city: { type: 'string', description: 'City name' } }, required: ['city'] },
  },
};

const CALCULATE_TOOL = {
  type: 'function',
  function: {
    name: 'calculate',
    description: 'Evaluate a math expression',
    parameters: { type: 'object', properties: { expression: { type: 'string', description: 'A math expression' } }, required: ['expression'] },
  },
};

function toolsFor(preset: ToolPreset): unknown[] {
  if (preset === 'weather') return [WEATHER_TOOL];
  if (preset === 'calculate') return [CALCULATE_TOOL];
  return [WEATHER_TOOL, CALCULATE_TOOL];
}

function toolChoiceArg(choice: 'auto' | string | undefined): unknown {
  if (!choice || choice === 'auto') return 'auto';
  return { type: 'function', function: { name: choice } };
}

function contentOf(payload: ChatCompletion): string {
  return payload.choices?.[0]?.message?.content ?? 'No completion text returned.';
}

async function readError(res: Response): Promise<string> {
  try {
    const body = (await res.json()) as { detail?: string; title?: string; error?: { message?: string } };
    return body.error?.message ?? body.detail ?? body.title ?? `Request failed (${res.status})`;
  } catch {
    return `Request failed (${res.status})`;
  }
}

export default App;
