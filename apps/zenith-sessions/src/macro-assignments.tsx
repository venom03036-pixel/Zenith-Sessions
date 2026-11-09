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

export function MacroAssignmentsPanel({ choice }: { choice: MacroChoice | null }) {
  const [view, setView] = useState<View | null>(null);
  const [busy, setBusy] = useState(true);
  const [notice, setNotice] = useState('');

  async function read(offset: number) {
    setBusy(true);
    setNotice('');
    try { setView(await invoke<View>('macro_assignments', { offset })); }
    catch (error) {
      setView(null);
      setNotice(typeof error === 'string' ? error : 'Could not read macro assignments.');
    } finally { setBusy(false); }
  }

  useEffect(() => { void read(0); }, []);

  async function edit(target: Target, change: Edit) {
    if (!view || busy) return;
    setBusy(true);
    setNotice('');
    try {
      setView(await invoke<View>('edit_macro_assignment', { target, stamp: view.stamp, offset: view.offset, edit: change }));
      setNotice(change.action === 'Save' ? 'Assignment saved.' : 'Assignment removed from this preview.');
    } catch (error) { setNotice(typeof error === 'string' ? error : 'Could not save the assignment change.'); }
    finally { setBusy(false); }
  }

  return <section className="account-panel preview-panel macro-catalog" aria-labelledby="macro-assignments-heading">
    <h2 id="macro-assignments-heading">Macro assignments</h2>
    <p>{choice ? <>Selected macro <strong>{choice.name}</strong></> : 'Select a saved macro from the catalog to assign it.'}</p>
    <p>Assignments are local references. Playback is unavailable in this preview.</p>
    <button className="secondary-button" disabled={busy} onClick={() => void read(0)}>Refresh assignments</button>
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
            <button className="secondary-button" disabled={busy || !choice} onClick={() => choice && void edit(row.target, { action: 'Save', choice: choice.binding })}>
              {row.assignment ? 'Replace assignment' : 'Assign selected macro'}
            </button>
            {row.assignment && <button className="secondary-button" disabled={busy} onClick={() => void edit(row.target, { action: 'Remove' })}>Remove assignment</button>}
          </div>}</td>
        </tr>)}</tbody>
      </table></div>
      <div className="row-actions">
        {view.previous !== null && <button className="secondary-button" disabled={busy} onClick={() => void read(view.previous!)}>Previous targets</button>}
        {view.next !== null && <button className="secondary-button" disabled={busy} onClick={() => void read(view.next!)}>Next targets</button>}
      </div>
    </>}
  </section>;
}
