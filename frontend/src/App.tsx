import { useEffect, useMemo, useState } from 'react';
import type { ChangeEvent, FormEvent } from 'react';
import './App.css';

type FoundryConfig = {
  endpoint: string;
  model: string;
};

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

type ModelsResponse = {
  current: string;
  available: ModelInfo[];
};

type ChatTurn = {
  role: 'user' | 'assistant';
  content: string;
};

// The capability panels a model can expose. "chat" covers text/code/reasoning (same transport,
// different starter prompt); vision/tools/audio are distinct request shapes.
type Mode = 'chat' | 'vision' | 'tools' | 'audio';

const STARTER_PROMPTS: Record<string, string> = {
  text: 'Explain why local OpenAI-compatible endpoints are useful.',
  code: 'Write a Python function `total(nums)` that returns the sum of a list of integers.',
  reasoning: 'If a train travels 60 km in 45 minutes, what is its average speed in km/h? Think step by step.',
};

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

function App() {
  const [config, setConfig] = useState<FoundryConfig | null>(null);
  const [models, setModels] = useState<ModelInfo[]>([]);
  const [selected, setSelected] = useState<string>('');
  const [mode, setMode] = useState<Mode>('chat');

  const [prompt, setPrompt] = useState(STARTER_PROMPTS.text);
  const [chat, setChat] = useState<ChatTurn[]>([]);
  const [imageDataUrl, setImageDataUrl] = useState<string | null>(null);
  const [audioFile, setAudioFile] = useState<File | null>(null);
  const [transcript, setTranscript] = useState<string | null>(null);

  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const currentModel = useMemo(
    () => models.find((m) => m.id === selected) ?? null,
    [models, selected],
  );
  const availableModes = useMemo(() => modesFor(currentModel), [currentModel]);

  // Load config + model catalog on startup.
  useEffect(() => {
    void (async () => {
      try {
        const [cfgRes, modelsRes] = await Promise.all([
          fetch('/api/foundry'),
          fetch('/api/models'),
        ]);
        if (!cfgRes.ok) throw new Error(`Could not load Foundry settings (${cfgRes.status})`);
        if (!modelsRes.ok) throw new Error(`Could not load model catalog (${modelsRes.status})`);
        const cfg = (await cfgRes.json()) as FoundryConfig;
        const list = (await modelsRes.json()) as ModelsResponse;
        setConfig(cfg);
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

  const resetPanels = () => {
    setChat([]);
    setImageDataUrl(null);
    setAudioFile(null);
    setTranscript(null);
    setError(null);
  };

  const onSelectModel = async (event: ChangeEvent<HTMLSelectElement>) => {
    const model = event.target.value;
    setSelected(model);
    resetPanels();
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

  const switchMode = (next: Mode) => {
    setMode(next);
    resetPanels();
    if (next === 'chat') {
      const kind = currentModel?.code ? 'code' : currentModel?.reasoning ? 'reasoning' : 'text';
      setPrompt(STARTER_PROMPTS[kind]);
    }
  };

  // ── Chat / Code / Reasoning ───────────────────────────────────────────────────
  const sendChat = async (event: FormEvent) => {
    event.preventDefault();
    if (!prompt.trim() || busy) return;
    setBusy(true);
    setError(null);
    const next: ChatTurn[] = [...chat, { role: 'user', content: prompt.trim() }];
    setChat(next);
    try {
      const res = await fetch('/v1/chat/completions', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ model: selected, messages: next }),
      });
      const message = await readChatMessage(res);
      setChat([...next, { role: 'assistant', content: message }]);
      setPrompt('');
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Completion error');
    } finally {
      setBusy(false);
    }
  };

  // ── Vision ────────────────────────────────────────────────────────────────────
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
      const res = await fetch('/v1/chat/completions', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          model: selected,
          messages: [
            {
              role: 'user',
              content: [
                { type: 'text', text: prompt.trim() },
                { type: 'image_url', image_url: { url: imageDataUrl } },
              ],
            },
          ],
        }),
      });
      const message = await readChatMessage(res);
      setChat([...next, { role: 'assistant', content: message }]);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Vision request error');
    } finally {
      setBusy(false);
    }
  };

  // ── Tools ─────────────────────────────────────────────────────────────────────
  const sendTools = async (event: FormEvent) => {
    event.preventDefault();
    if (!prompt.trim() || busy) return;
    setBusy(true);
    setError(null);
    const next: ChatTurn[] = [...chat, { role: 'user', content: prompt.trim() }];
    setChat(next);
    try {
      const res = await fetch('/v1/chat/completions', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          model: selected,
          messages: next,
          tools: [WEATHER_TOOL],
          tool_choice: 'auto',
        }),
      });
      if (!res.ok) throw new Error(await readError(res));
      const payload = (await res.json()) as ToolCompletion;
      const choice = payload.choices?.[0]?.message;
      const calls = choice?.tool_calls ?? [];
      const text = calls.length > 0
        ? `🛠 tool_calls:\n${calls.map((c) => `${c.function?.name}(${c.function?.arguments})`).join('\n')}`
        : choice?.content ?? 'No content returned.';
      setChat([...next, { role: 'assistant', content: text }]);
      setPrompt('');
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Tool request error');
    } finally {
      setBusy(false);
    }
  };

  // ── Audio / speech-to-text ──────────────────────────────────────────────────────
  const sendAudio = async (event: FormEvent) => {
    event.preventDefault();
    if (!audioFile || busy) return;
    setBusy(true);
    setError(null);
    setTranscript(null);
    try {
      const form = new FormData();
      form.append('model', selected);
      form.append('file', audioFile);
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
              onClick={() => switchMode(m)}
              disabled={busy}
            >
              {MODE_LABELS[m]}
            </button>
          ))}
        </nav>
      )}

      {error && <p className="error">{error}</p>}

      {mode === 'chat' && (
        <ChatLikePanel
          prompt={prompt}
          setPrompt={setPrompt}
          onSubmit={sendChat}
          busy={busy}
          ready={!!config}
          chat={chat}
        />
      )}

      {mode === 'vision' && (
        <section className="panel" aria-label="Vision">
          <form onSubmit={sendVision} className="prompt-form">
            <input type="file" accept="image/*" onChange={onPickImage} disabled={busy} aria-label="Image" />
            {imageDataUrl && <img className="preview" src={imageDataUrl} alt="selected" />}
            <textarea
              value={prompt}
              onChange={(e) => setPrompt(e.target.value)}
              placeholder="Ask about the image"
              rows={3}
              disabled={busy}
            />
            <button type="submit" disabled={busy || !imageDataUrl}>
              {busy ? 'Running...' : 'Describe Image'}
            </button>
          </form>
          <ChatLog chat={chat} />
        </section>
      )}

      {mode === 'tools' && (
        <section className="panel" aria-label="Tools">
          <p className="hint">Exposes a <code>get_weather(city)</code> function. The model may answer in text or emit a tool call.</p>
          <form onSubmit={sendTools} className="prompt-form">
            <textarea
              value={prompt}
              onChange={(e) => setPrompt(e.target.value)}
              placeholder="e.g. What's the weather in Paris?"
              rows={3}
              disabled={busy}
            />
            <button type="submit" disabled={busy}>{busy ? 'Running...' : 'Send with Tools'}</button>
          </form>
          <ChatLog chat={chat} />
        </section>
      )}

      {mode === 'audio' && (
        <section className="panel" aria-label="Speech-to-text">
          <form onSubmit={sendAudio} className="prompt-form">
            <input
              type="file"
              accept="audio/*,.wav,.mp3,.m4a,.flac,.ogg"
              onChange={(e) => setAudioFile(e.target.files?.[0] ?? null)}
              disabled={busy}
              aria-label="Audio file"
            />
            <button type="submit" disabled={busy || !audioFile}>{busy ? 'Transcribing...' : 'Transcribe'}</button>
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

// Shared text-chat panel — preserves the selectors used by the Playwright e2e suite.
function ChatLikePanel(props: {
  prompt: string;
  setPrompt: (v: string) => void;
  onSubmit: (e: FormEvent) => void;
  busy: boolean;
  ready: boolean;
  chat: ChatTurn[];
}) {
  return (
    <section className="panel" aria-label="Text chat">
      <form onSubmit={props.onSubmit} className="prompt-form">
        <textarea
          value={props.prompt}
          onChange={(e) => props.setPrompt(e.target.value)}
          placeholder="Enter a prompt"
          rows={4}
          disabled={props.busy}
        />
        <button type="submit" disabled={props.busy || !props.ready}>
          {props.busy ? 'Running...' : 'Send Prompt'}
        </button>
      </form>
      <ChatLog chat={props.chat} />
    </section>
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

type ToolCall = { function?: { name?: string; arguments?: string } };
type ToolCompletion = {
  choices?: Array<{ message?: { content?: string; tool_calls?: ToolCall[] } }>;
};

const WEATHER_TOOL = {
  type: 'function',
  function: {
    name: 'get_weather',
    description: 'Get the current weather for a city',
    parameters: {
      type: 'object',
      properties: { city: { type: 'string' } },
      required: ['city'],
    },
  },
};

async function readError(res: Response): Promise<string> {
  try {
    const body = (await res.json()) as { detail?: string; title?: string; error?: { message?: string } };
    return body.error?.message ?? body.detail ?? body.title ?? `Request failed (${res.status})`;
  } catch {
    return `Request failed (${res.status})`;
  }
}

async function readChatMessage(res: Response): Promise<string> {
  if (!res.ok) throw new Error(await readError(res));
  const payload = (await res.json()) as { choices?: Array<{ message?: { content?: string } }> };
  return payload.choices?.[0]?.message?.content ?? 'No completion text returned.';
}

export default App;
