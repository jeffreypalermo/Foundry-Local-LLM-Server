import { useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import './App.css';

type FoundryConfig = {
  endpoint: string;
  model: string;
};

type ChatTurn = {
  role: 'user' | 'assistant';
  content: string;
};

function App() {
  const [config, setConfig] = useState<FoundryConfig | null>(null);
  const [prompt, setPrompt] = useState('Explain why local OpenAI-compatible endpoints are useful.');
  const [chat, setChat] = useState<ChatTurn[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    void fetch('/api/foundry')
      .then((response) => {
        if (!response.ok) {
          throw new Error(`Could not load Foundry Local settings (${response.status})`);
        }

        return response.json() as Promise<FoundryConfig>;
      })
      .then(setConfig)
      .catch((err: unknown) => {
        const message = err instanceof Error ? err.message : 'Unknown startup error';
        setError(message);
      });
  }, []);

  const submitPrompt = async (event: FormEvent) => {
    event.preventDefault();
    if (!prompt.trim() || !config) {
      return;
    }

    setBusy(true);
    setError(null);

    const nextChat: ChatTurn[] = [...chat, { role: 'user', content: prompt.trim() }];
    setChat(nextChat);

    try {
      const response = await fetch('/v1/chat/completions', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({
          model: config.model,
          messages: nextChat,
        }),
      });

      if (!response.ok) {
        throw new Error(`Completion failed (${response.status})`);
      }

      const payload = await response.json() as { choices?: Array<{ message?: { content?: string } }> };
      const message = payload.choices?.[0]?.message?.content ?? 'No completion text returned.';

      setChat([...nextChat, { role: 'assistant', content: message }]);
      setPrompt('');
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : 'Unknown completion error';
      setError(message);
      setChat(nextChat);
    } finally {
      setBusy(false);
    }
  };

  return (
    <main className="app-shell">
      <h1>Foundry Local OpenAI Server</h1>
      <p className="config-line">
        Model: <strong>{config?.model ?? 'loading...'}</strong>
      </p>
      <p className="config-line">
        Foundry endpoint: <code>{config?.endpoint ?? 'loading...'}</code>
      </p>
      <p className="config-line">
        OpenAI-compatible endpoint for tools: <code>/v1/chat/completions</code>
      </p>

      <form onSubmit={submitPrompt} className="prompt-form">
        <textarea
          value={prompt}
          onChange={(event) => setPrompt(event.target.value)}
          placeholder="Enter a prompt"
          rows={4}
          disabled={busy}
        />
        <button type="submit" disabled={busy || !config}>
          {busy ? 'Running...' : 'Send Prompt'}
        </button>
      </form>

      {error && <p className="error">{error}</p>}

      <section className="chat-log" aria-label="Chat transcript">
        {chat.map((entry, index) => (
          <article key={index} className={`message ${entry.role}`}>
            <h2>{entry.role === 'user' ? 'User' : 'Assistant'}</h2>
            <p>{entry.content}</p>
          </article>
        ))}
      </section>
    </main>
  );
}

export default App;
