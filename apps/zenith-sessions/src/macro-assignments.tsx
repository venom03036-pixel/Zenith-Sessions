import { invoke } from '@tauri-apps/api/core';
import { useEffect, useState } from 'react';
import type { MacroChoice } from './macro-catalog';

type Target = { kind: 'Account' | 'Group' | 'Bundle'; id: string };
type Row = {
  target: Target; name: string; editable: boolean; inherited: boolean;
  assignment: { id: string; name: string; savedRevision: string | null } | null;
};
type View = {
  stamp: string; total: number; offset: number; previous: number | null; next: number | null;
  unreadableLegacy: number; rows: Row[];
};
type Edit = { action: 'Save'; choice: MacroChoice['binding'] } | { action: 'Remove' };
type Session =
  | { status: 'noSession' | 'arrivalUnconfirmed' | 'windowUnavailable' }
  | { status: 'ready'; attemptId: string; pid: number };
type TargetReview = { targetName: string; macroName: string; recipients: { name: string; session: Session | null }[] };
type Review = { choice: MacroChoice; stamp: string; epoch: string } & ({ value: TargetReview } | { error: string });
const statusLabel = {
  noSession: 'No active session',
  arrivalUnconfirmed: 'Initial arrival unconfirmed',
  windowUnavailable: 'Owned window unavailable or ambiguous',
  ready: 'Owned window available',
};

export function MacroAssignmentsPanel({ choice, epoch }: { choice: MacroChoice | null; epoch: string | null }) {
  const [view, setView] = useState<View | null>(null);
  const [busy, setBusy] = useState(true);
  const [notice, setNotice] = useState('');
  const [reviewing, setReviewing] = useState(false);
  const [review, setReview] = useState<Review | null>(null);
  const currentReview = review && review.choice === choice && review.stamp === view?.stamp && review.epoch === epoch
    ? review : null;

  async function read(offset: number) {
    setBusy(true);
    setNotice('');
    setReview(null);
    try { setView(await invoke<View>('macro_assignments', { offset })); }
    catch (error) {
      setView(null);
      setNotice(typeof error === 'string' ? error : 'Could not read macro assignments.');
    } finally { setBusy(false); }
  }

  useEffect(() => { void read(0); }, []);

  async function edit(target: Target, change: Edit) {
    if (!view || busy || reviewing) return;
    setBusy(true);
    setNotice('');
    setReview(null);
    try {
      setView(await invoke<View>('edit_macro_assignment', { target, stamp: view.stamp, offset: view.offset, edit: change }));
      setNotice(change.action === 'Save' ? 'Assignment saved.' : 'Assignment removed from this preview.');
    } catch (error) { setNotice(typeof error === 'string' ? error : 'Could not save the assignment change.'); }
    finally { setBusy(false); }
  }

  async function reviewTargets(target: Target) {
    if (!view || !choice || epoch === null || busy || reviewing) return;
    const { stamp } = view;
    const context = { choice, stamp, epoch };
    setReview(null);
    setReviewing(true);
    try {
      const value = await invoke<TargetReview>('review_macro_targets', { target, stamp, choice: choice.binding, epoch });
      setReview({ ...context, value });
    } catch (error) {
      setReview({ ...context, error: typeof error === 'string' ? error : 'Could not review playback targets.' });
    } finally { setReviewing(false); }
  }

  return <section className="account-panel preview-panel macro-catalog" aria-labelledby="macro-assignments-heading">
    <h2 id="macro-assignments-heading">Macro assignments</h2>
    <p>{choice ? <>Selected macro <strong>{choice.name}</strong></> : 'Select a saved macro from the catalog to assign it.'}</p>
    <p>Select the assigned macro in the catalog to review its recipients. Playback is unavailable in this preview.</p>
    <button className="secondary-button" disabled={busy || reviewing} onClick={() => void read(0)}>Refresh assignments</button>
    {busy && <p role="status">Updating assignments…</p>}
    {notice && <p role="status">{notice}</p>}
    {view?.unreadableLegacy ? <p>{view.unreadableLegacy} unreadable references are preserved in the original archive.</p> : null}
    {view?.total === 0 && <p>Add a saved account or import a legacy workspace to see assignment targets.</p>}
    {view && view.rows.length > 0 && <>
      <p>Showing {view.offset + 1}–{view.offset + view.rows.length} of {view.total} targets</p>
      <div className="table-wrap"><table>
        <thead><tr><th>Target</th><th>Assigned macro</th><th>Actions</th></tr></thead>
        <tbody>{view.rows.map(row => <tr key={`${row.target.kind}:${row.target.id}:${row.editable}:${row.inherited && !row.editable}`}>
          <td>{row.name} · {row.target.kind}{!row.editable && ' · Archived'}</td>
          <td>{row.assignment?.name ?? 'Unassigned'}{row.inherited && ' · Imported'}</td>
          <td>{row.editable && <div className="row-actions">
            <button className="secondary-button" disabled={busy || reviewing || !choice} onClick={() => choice && void edit(row.target, { action: 'Save', choice: choice.binding })}>
              {row.assignment ? 'Replace assignment' : 'Assign selected macro'}
            </button>
            {row.assignment && <>
              <button className="secondary-button" disabled={busy || reviewing} onClick={() => void edit(row.target, { action: 'Remove' })}>Remove assignment</button>
              <button className="secondary-button" disabled={busy || reviewing || epoch === null || choice?.binding.macroId !== row.assignment.id}
                onClick={() => void reviewTargets(row.target)}>Review playback targets</button>
            </>}
          </div>}</td>
        </tr>)}</tbody>
      </table></div>
      <div className="row-actions">
        {view.previous !== null && <button className="secondary-button" disabled={busy || reviewing} onClick={() => void read(view.previous!)}>Previous targets</button>}
        {view.next !== null && <button className="secondary-button" disabled={busy || reviewing} onClick={() => void read(view.next!)}>Next targets</button>}
      </div>
    </>}
    {reviewing && <p role="status">Checking playback targets…</p>}
    {currentReview && ('error' in currentReview
      ? <p role="status">{currentReview.error}</p>
      : <div role="status">
        <p><strong>{currentReview.value.macroName}</strong> · {currentReview.value.targetName}</p>
        <p>Window and arrival evidence at the time of review.</p>
        {currentReview.value.recipients.length ? <ul>
          {currentReview.value.recipients.map(({ name, session }, index) => <li key={index}>
            {name} · {session ? statusLabel[session.status] : 'Missing or unlinked imported profile'}
            {session?.status === 'ready' && ` (PID ${session.pid})`}
          </li>)}
        </ul> : <p>This assignment has no recipients.</p>}
      </div>)}
  </section>;
}
