import { invoke } from '@tauri-apps/api/core';
import { useEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';
import './diagnostics.css';

type Admission = {
  limitingResource: 'stale' | 'invalid' | 'commit' | 'physical_memory' | null;
  physicalAvailable: number;
  physicalRequired: number;
  commitAvailable: number;
  commitRequired: number;
  canTryOne: boolean;
};

const status: Record<NonNullable<Admission['limitingResource']>, string> = {
  stale: 'The memory reading expired. Refresh it to try again.',
  invalid: 'Windows returned an invalid memory reading.',
  commit: 'Waiting for committed memory headroom.',
  physical_memory: 'Waiting for physical memory headroom.',
};

function gib(bytes: number) {
  return `${(bytes / 2 ** 30).toFixed(2)} GiB`;
}

function Diagnostics() {
  const [reading, setReading] = useState<Admission | null>(null);
  const [error, setError] = useState(false);
  const [loading, setLoading] = useState(true);

  async function refresh() {
    setLoading(true);
    setError(false);
    setReading(null);
    try {
      setReading(await invoke<Admission>('read_admission'));
    } catch {
      setError(true);
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => { void refresh(); }, []);

  return (
    <main>
      <p className="eyebrow">Zenith Sessions · Development probe</p>
      <h1>Launch diagnostics</h1>
      <section aria-label="Launch memory reading" aria-busy={loading}>
        <h2>Memory for one new client</h2>
        <div role="status">
          {loading && <p>Reading Windows memory…</p>}
          {error && <p>Windows memory is unavailable. Open this screen in the Zenith desktop app and try again.</p>}
          {reading && <>
            <p>{reading.limitingResource ? status[reading.limitingResource] : 'Memory headroom is available.'}</p>
            <dl>
              <dt>Physical memory</dt>
              <dd>{gib(reading.physicalAvailable)} available · {gib(reading.physicalRequired)} required</dd>
              <dt>Commit</dt>
              <dd>{gib(reading.commitAvailable)} available · {gib(reading.commitRequired)} required</dd>
            </dl>
            {reading.canTryOne && <p>A single launch trial can be requested with the explicit memory override.</p>}
          </>}
        </div>
        <button disabled={loading} onClick={() => void refresh()}>Refresh memory reading</button>
      </section>
    </main>
  );
}

createRoot(document.getElementById('root')!).render(<Diagnostics />);
