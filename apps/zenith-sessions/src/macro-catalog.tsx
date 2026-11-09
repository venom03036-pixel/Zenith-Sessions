import { invoke } from '@tauri-apps/api/core';
import { useState } from 'react';

type Cursor = { offset: number; hostSessionId: string; catalogRevision: string; total: number };
const targetLabels = {
  Legacy: 'Legacy target', Screen: 'Screen', ActiveExternalWindow: 'Active external window',
  Application: 'Application', SpecificWindow: 'Specific window',
};
type Descriptor = {
  id: string; savedRevision: string; name: string; hotkey: string;
  topLevelActions: number; enabled: boolean; targetMode: keyof typeof targetLabels;
};
const messages = {
  Starting: 'The Personal editor is loading its saved macro catalog.',
  Changed: 'The catalog changed. Refresh to start from the first page.',
  Unavailable: 'Zenith could not confirm the catalog. Reload it in the Personal editor, then refresh.',
  Incompatible: 'The Personal editor returned an incompatible catalog.',
  Disconnected: 'Open the Personal editor, then refresh the catalog.',
  Busy: 'The catalog is busy. Wait a moment and refresh again.',
};
type Page = {
  state: 'Ready' | keyof typeof messages; hostVersion: string; items: Descriptor[];
  total: number; offset: number; previous: Cursor | null; next: Cursor | null;
};

export function MacroCatalogPanel({ assignmentCount }: { assignmentCount?: number }) {
  const [page, setPage] = useState<Page | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');

  async function read(cursor: Cursor | null) {
    setLoading(true);
    setPage(null);
    setError('');
    try { setPage(await invoke<Page>('saved_macro_catalog', { cursor })); }
    catch (error) { setError(typeof error === 'string' ? error : 'Zenith could not read the catalog.'); }
    finally { setLoading(false); }
  }

  return <section className="account-panel preview-panel macro-catalog" aria-labelledby="macro-catalog-heading">
    <h2 id="macro-catalog-heading">Saved macro catalog</h2>
    <p>Read saved macro details from the Zenith Macros Personal editor. Assignment editing and playback are unavailable in this preview.</p>
    {assignmentCount !== undefined && <p>{assignmentCount} legacy assignment {assignmentCount === 1 ? 'reference' : 'references'} preserved.</p>}
    <button className="secondary-button" disabled={loading} onClick={() => void read(null)}>Refresh catalog</button>
    {loading && <p role="status">Reading the Personal editor catalog…</p>}
    {error && <p role="status">{error}</p>}
    {!loading && !error && !page && <p>Open the Personal editor and choose Refresh catalog.</p>}
    {page && page.state !== 'Ready' && <p role="status">{messages[page.state]}</p>}
    {page?.state === 'Ready' && <>
      <p role="status">Personal editor {page.hostVersion} · {page.total ? `Showing ${page.offset + 1}–${page.offset + page.items.length} of ${page.total}` : 'No saved macros'}</p>
      {page.items.length > 0 && <div className="table-wrap"><table>
        <thead><tr><th>Macro</th><th>Target mode</th><th>Hotkey</th><th>Actions</th><th>Status</th></tr></thead>
        <tbody>{page.items.map(item => <tr key={item.id}>
          <td>{item.name}</td><td>{targetLabels[item.targetMode]}</td><td>{item.hotkey || 'Unassigned'}</td>
          <td>{item.topLevelActions}</td><td>{item.enabled ? 'Enabled' : 'Disabled'}</td>
        </tr>)}</tbody>
      </table></div>}
      <div className="row-actions">
        {page.previous && <button className="secondary-button" onClick={() => void read(page.previous)}>Previous page</button>}
        {page.next && <button className="secondary-button" onClick={() => void read(page.next)}>Next page</button>}
      </div>
    </>}
  </section>;
}
