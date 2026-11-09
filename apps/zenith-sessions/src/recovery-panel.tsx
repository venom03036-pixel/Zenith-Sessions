type RecoveryState = 'interrupted' | 'unknown' | 'surviving' | 'ended';
type Attempt = {
  attemptId: string;
  username: string;
  placeId: string;
  requestedServer: string | null;
  pid: number | null;
  state: RecoveryState;
};
export type Recovery = { issue: string | null; canArchive: boolean; attempts: Attempt[] };

const descriptions: Record<RecoveryState, string> = {
  interrupted: 'Interrupted before native launch.',
  unknown: 'Startup or process identity is uncertain. Close other Roblox windows and launchers before dismissing.',
  surviving: 'The recorded process is still running. Close it from its own window or taskbar entry.',
  ended: 'The recorded process is no longer running.',
};

export function RecoveryPanel({ recovery, onDismiss, onArchive }: {
  recovery: Recovery;
  onDismiss: (attempt: string) => void;
  onArchive: () => void;
}) {
  return <section className="account-panel preview-panel" aria-label="Launch recovery">
    <h2>Launch recovery</h2>
    <p>These entries are from a previous run. Zenith checks process identity separately from Roblox sign-in and arrival.</p>
    {recovery.issue && <p role="status">{recovery.issue}</p>}
    {recovery.canArchive && <>
      <p>Close other Roblox clients and launchers first. The original journal will be kept as an archived file.</p>
      <button className="secondary-button" onClick={onArchive}>Archive unreadable journal</button>
    </>}
    {recovery.attempts.map(attempt => <article className="recovery-attempt" key={attempt.attemptId}>
      <h3>{attempt.username}</h3>
      <p>Requested place {attempt.placeId}{attempt.pid ? ` · PID ${attempt.pid}` : ''}</p>
      {attempt.requestedServer && <p>Requested server {attempt.requestedServer}</p>}
      <p>{descriptions[attempt.state]}</p>
      {attempt.state !== 'surviving' && <button className="secondary-button"
        onClick={() => onDismiss(attempt.attemptId)}>{attempt.state === 'unknown' ? 'Check and dismiss' : 'Dismiss'}</button>}
    </article>)}
  </section>;
}
