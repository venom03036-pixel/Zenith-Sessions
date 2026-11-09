import { useState } from 'react';

type ServerVisit = { placeId: string; serverId: string; observedAt: number };
export type ServerHistory = { visits: ServerVisit[]; issue: string | null };

export function RecentServers({ history, disabled, onSelect, onClear }: {
  history?: ServerHistory;
  disabled: boolean;
  onSelect: (visit: ServerVisit) => void;
  onClear: () => Promise<boolean>;
}) {
  const [confirmClear, setConfirmClear] = useState(false);
  const [clearing, setClearing] = useState(false);
  const visits = history?.visits ?? [];

  async function clear() {
    setClearing(true);
    try {
      if (await onClear()) setConfirmClear(false);
    } finally { setClearing(false); }
  }

  return <details className="recent-servers" aria-label="Recently joined public servers">
    <summary>Recently joined public servers{visits.length ? ` (${visits.length})` : ''}</summary>
    <div className="recent-servers-content">
      {history?.issue && <p role="status">{history.issue}</p>}
      {visits.length ? <>
        <p>Choose a past arrival to fill the exact place and server. Roblox assigns seats when you join.</p>
        <ul className="directory-results" aria-label="Recent server visits">
          {visits.map(visit => {
            const observed = new Date(visit.observedAt * 1000);
            return <li key={`${visit.placeId}:${visit.serverId}`}>
              <button className="secondary-button" disabled={disabled} onClick={() => onSelect(visit)}>
                <span>Place {visit.placeId} · <code>{visit.serverId}</code></span>
                <time dateTime={observed.toISOString()}>{observed.toLocaleString()}</time>
              </button>
            </li>;
          })}
        </ul>
      </> : <p>{history ? 'No confirmed public server visits yet.' : 'Reading server history…'}</p>}
      {confirmClear ? <section className="inline-alert" role="dialog" aria-label="Clear recent server history">
        <p>Clear the recent server history on this PC?</p>
        <div className="row-actions">
          <button className="secondary-button" autoFocus disabled={clearing} onClick={() => setConfirmClear(false)}>Cancel clear</button>
          <button className="primary-button" disabled={disabled || clearing} onClick={() => void clear()}>{clearing ? 'Clearing…' : 'Clear history'}</button>
        </div>
      </section> : (visits.length > 0 || history?.issue) &&
        <button className="secondary-button" disabled={disabled} onClick={() => setConfirmClear(true)}>Clear recent servers</button>}
    </div>
  </details>;
}
