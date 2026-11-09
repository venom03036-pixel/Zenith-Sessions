import { invoke } from '@tauri-apps/api/core';
import { useState } from 'react';

export function SupportReportPanel() {
  const [saving, setSaving] = useState(false);
  const [path, setPath] = useState('');
  const [error, setError] = useState('');

  async function save() {
    setSaving(true);
    setError('');
    setPath('');
    try {
      const report = await invoke<{ path: string }>('export_support_report');
      setPath(report.path);
    } catch (reason) {
      setError(typeof reason === 'string' ? reason : 'Could not save the support report.');
    } finally {
      setSaving(false);
    }
  }

  return <section className="account-panel preview-panel" aria-label="Support report">
    <h2>Support report</h2>
    <p>Save a local JSON file with this build’s identity, memory checks, recent launch stages, and recovery status.
      The report excludes account names, IDs, credentials, invite links, and raw logs.</p>
    <button className="secondary-button" disabled={saving} onClick={() => void save()}>
      {saving ? 'Saving support report…' : 'Save support report'}
    </button>
    {error && <p role="alert">{error}</p>}
    {path && <div className="session-fields support-report-path">
      <label><span role="status">Report saved. Copy this path to find the JSON file.</span>
        <input aria-label="Saved support report path" readOnly value={path} onFocus={event => event.currentTarget.select()}/>
      </label>
    </div>}
  </section>;
}
