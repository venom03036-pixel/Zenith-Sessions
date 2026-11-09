import { invoke } from '@tauri-apps/api/core';
import { useEffect, useState } from 'react';

const priorityLabels = {
  idle: 'Idle', below_normal: 'Below normal', normal: 'Normal',
  above_normal: 'Above normal', high: 'High', realtime: 'Realtime',
};
type Priority = { class: keyof typeof priorityLabels; canLower: boolean; canRestore: boolean };
const efficiencyLabels = {
  system_managed: 'System-managed',
  efficiency_requested: 'Efficiency requested',
  performance_requested: 'Performance requested',
};
type Efficiency = { policy: keyof typeof efficiencyLabels; canRequest: boolean; canRestore: boolean };
type Metrics = {
  cpuPercent: number | null; readBytesPerSecond: number | null; writeBytesPerSecond: number | null;
  workingSet: number | null; privateWorkingSet: number | null; privateCommit: number | null;
  uptimeSeconds: number | null; priority: Priority | null;
  efficiency: Efficiency | null;
};
type Client = { attemptId: string; username: string; pid: number | null; metrics: Metrics | null };

function units(value: number | null | undefined, divisor: number, suffix: string) {
  return value == null ? 'Unavailable' : `${(value / divisor).toFixed(1)} ${suffix}`;
}

export function ClientPerformance() {
  const [clients, setClients] = useState<Client[] | null>(null);
  const [unavailable, setUnavailable] = useState(false);

  useEffect(() => {
    let active = true;
    let pending = false;
    async function sample() {
      if (pending || document.hidden) return;
      pending = true;
      try {
        const rows = await invoke<Client[]>('client_performance');
        if (active && !document.hidden) {
          setClients(rows);
          setUnavailable(false);
        }
      } catch {
        if (active) {
          setClients(null);
          setUnavailable(true);
        }
      } finally { pending = false; }
    }
    function onVisibility() {
      if (document.hidden) setClients(null);
      else void sample();
    }
    void sample();
    const timer = window.setInterval(() => void sample(), 1000);
    document.addEventListener('visibilitychange', onVisibility);
    return () => {
      active = false;
      window.clearInterval(timer);
      document.removeEventListener('visibilitychange', onVisibility);
    };
  }, []);

  return <>
    <section className="account-panel preview-panel">
      <h2>Client measurements</h2>
      <p>CPU is a share of total system capacity. I/O includes process transfers across files, devices, and the network. Rates need two recent samples.</p>
      {unavailable ? <p role="status">Windows client measurements are unavailable.</p>
        : !clients ? <p>Waiting for a fresh reading…</p>
        : !clients.length && <p>Measurements appear here after a client’s arrival is confirmed.</p>}
    </section>
    {clients && clients.length > 0 && <section className="window-cards" aria-label="Client measurements by account">
      {clients.map(client => <ClientMeasurements key={client.attemptId} client={client}/>)}
    </section>}
  </>;
}

function ClientMeasurements({ client }: { client: Client }) {
  const [changing, setChanging] = useState(false);
  const [notice, setNotice] = useState('');
  const metrics = client.metrics;
  const priority = metrics?.priority;
  const efficiency = metrics?.efficiency;

  async function change(action: 'lower' | 'restore') {
    setChanging(true);
    setNotice('');
    try {
      const result = await invoke<Priority>('set_client_priority', { attempt: client.attemptId, action });
      setNotice(`Windows confirmed ${priorityLabels[result.class]} priority.`);
    } catch (error) { setNotice(typeof error === 'string' ? error : 'Could not change client priority.'); }
    finally { setChanging(false); }
  }

  async function changeEfficiency(action: 'request' | 'restore') {
    setChanging(true);
    setNotice('');
    try {
      const result = await invoke<Efficiency>('set_client_efficiency', { attempt: client.attemptId, action });
      setNotice(`Windows confirmed the efficiency policy. ${efficiencyLabels[result.policy]}.`);
    } catch (error) { setNotice(typeof error === 'string' ? error : 'Could not change client efficiency.'); }
    finally { setChanging(false); }
  }

  return <article className="account-panel preview-panel">
    <h2>{client.username}</h2>
    {client.pid && <p>PID {client.pid}</p>}
    <dl className="memory-grid">
      <dt>CPU</dt><dd>{metrics?.cpuPercent == null ? 'Unavailable' : `${metrics.cpuPercent.toFixed(1)}%`}</dd>
      <dt>Working set</dt><dd>{units(metrics?.workingSet, 2 ** 20, 'MiB')}</dd>
      <dt>Private working set</dt><dd>{units(metrics?.privateWorkingSet, 2 ** 20, 'MiB')}</dd>
      <dt>Private commit</dt><dd>{units(metrics?.privateCommit, 2 ** 20, 'MiB')}</dd>
      <dt>I/O reads</dt><dd>{units(metrics?.readBytesPerSecond, 1024, 'KiB/s')}</dd>
      <dt>I/O writes</dt><dd>{units(metrics?.writeBytesPerSecond, 1024, 'KiB/s')}</dd>
      <dt>Uptime</dt><dd>{metrics?.uptimeSeconds == null ? 'Unavailable' : `${metrics.uptimeSeconds.toLocaleString()} s`}</dd>
      <dt>Windows priority</dt><dd>{priority ? priorityLabels[priority.class] : 'Unavailable'}</dd>
      <dt>Windows efficiency</dt><dd>{efficiency ? efficiencyLabels[efficiency.policy] : 'Unavailable'}</dd>
    </dl>
    <div className="row-actions">
      {priority?.canLower && <button className="secondary-button" disabled={changing} onClick={() => void change('lower')}>Set Below normal</button>}
      {priority?.canRestore && <button className="secondary-button" disabled={changing} onClick={() => void change('restore')}>Restore original priority</button>}
      {efficiency?.canRequest && <button className="secondary-button" disabled={changing} onClick={() => void changeEfficiency('request')}>Request Windows efficiency</button>}
      {efficiency?.canRestore && <button className="secondary-button" disabled={changing} onClick={() => void changeEfficiency('restore')}>Restore system-managed efficiency</button>}
    </div>
    {notice && <p role="status">{notice}</p>}
  </article>;
}
