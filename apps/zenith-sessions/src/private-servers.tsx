import { invoke } from '@tauri-apps/api/core';
import { useEffect, useRef, useState } from 'react';

type Reference = { id: string; revision: string };
export type PrivateServer = Reference & { name: string; placeId: string };

export function privateReference({ id, revision }: PrivateServer): Reference {
  return { id, revision };
}

export function PrivateServers({ account, disabled, selected, onSelect, onRecordChange }: {
  account: string | undefined;
  disabled: boolean;
  selected: PrivateServer | null;
  onSelect: (server: PrivateServer | null) => void;
  onRecordChange: (id: string, server: PrivateServer | null) => void;
}) {
  const [rows, setRows] = useState<PrivateServer[] | null>(null);
  const [name, setName] = useState('');
  const [invite, setInvite] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [reload, setReload] = useState(0);
  const [edit, setEdit] = useState<{ row: PrivateServer; name: string } | null>(null);
  const [remove, setRemove] = useState<PrivateServer | null>(null);
  const mounted = useRef(false);

  useEffect(() => {
    mounted.current = true;
    let current = true;
    void invoke<PrivateServer[]>('private_servers').then(
      value => { if (current) setRows(value); },
      reason => { if (current) setError(message(reason)); },
    );
    return () => { current = false; mounted.current = false; };
  }, [reload]);

  async function perform<T>(command: string, args: Record<string, unknown>, success: (result: T) => void) {
    setBusy(true);
    setError('');
    try {
      const result = await invoke<T>(command, args);
      if (mounted.current) success(result);
    } catch (reason) {
      if (mounted.current) setError(message(reason));
    } finally {
      if (mounted.current) setBusy(false);
    }
  }

  const locked = disabled || busy || rows === null;
  return <details className="private-servers" aria-label="Private servers"
    onToggle={event => { if (!event.currentTarget.open) setInvite(''); }}>
    <summary>Private servers</summary>
    <div className="private-invite-content">
      <p>Save a Roblox private invite with a local name. Select an account first to check a Roblox Server share link.</p>
      <form className="private-invite-form" onSubmit={event => {
        event.preventDefault();
        if (locked || !name.trim() || !invite.trim()) return;
        void perform<PrivateServer>('save_private_server', { name, invite, account: account ?? null }, row => {
          setRows(previous => [...(previous ?? []), row]);
          setName('');
          setInvite('');
        });
      }}>
        <label>Private server name<input aria-label="Private server name" value={name} maxLength={80} disabled={locked}
          onChange={event => setName(event.target.value)}/></label>
        <label>Private Roblox invite link<input aria-label="Private Roblox invite link" type="password" autoComplete="off"
          value={invite} maxLength={768} disabled={locked} onChange={event => setInvite(event.target.value)}/></label>
        <button className="secondary-button" disabled={locked || !name.trim() || !invite.trim()}>Save private invite</button>
      </form>
      {error && <p role="alert">{error}</p>}
      {busy && <p role="status">Updating private invites…</p>}
      {!rows && error && <button className="secondary-button" onClick={() => { setError(''); setReload(value => value + 1); }}>Retry private list</button>}
      {!rows && !error && <p role="status">Loading private invites…</p>}
      {rows?.length === 0 && <p>No private invites saved.</p>}
      {!!rows?.length && <ul className="private-invite-list">{rows.map(row => <li key={row.id}>
        <div><strong>{row.name}</strong><span>Place {row.placeId}</span></div>
        <div className="row-actions">
          <button className="secondary-button" disabled={locked} aria-pressed={selected?.id === row.id}
            onClick={() => onSelect(row)}>Use {row.name}</button>
          <button className="secondary-button" disabled={locked} aria-label={`Rename ${row.name}`}
            onClick={() => { setRemove(null); setEdit({ row, name: row.name }); }}>Rename</button>
          <button className="secondary-button" disabled={locked} aria-label={`Remove ${row.name}`}
            onClick={() => { setEdit(null); setRemove(row); }}>Remove</button>
        </div>
        {edit?.row.id === row.id && <form className="row-actions" onSubmit={event => {
          event.preventDefault();
          if (locked || !edit.name.trim()) return;
          void perform<PrivateServer>('rename_private_server', { reference: privateReference(row), name: edit.name }, updated => {
            setRows(previous => previous?.map(item => item.id === row.id ? updated : item) ?? []);
            onRecordChange(row.id, updated);
            setEdit(null);
          });
        }}>
          <input aria-label="New private server name" maxLength={80} value={edit.name} disabled={locked}
            onChange={event => setEdit({ row, name: event.target.value })}/>
          <button className="secondary-button" disabled={locked || !edit.name.trim()}>Save name</button>
          <button type="button" className="secondary-button" onClick={() => setEdit(null)}>Cancel rename</button>
        </form>}
        {remove?.id === row.id && <div className="row-actions">
          <span>Remove this saved invite?</span>
          <button className="secondary-button" disabled={locked} onClick={() => void perform<void>(
            'remove_private_server', { reference: privateReference(row) }, () => {
              setRows(previous => previous?.filter(item => item.id !== row.id) ?? []);
              onRecordChange(row.id, null);
              setRemove(null);
            })}>Remove invite</button>
          <button className="secondary-button" onClick={() => setRemove(null)}>Cancel removal</button>
        </div>}
      </li>)}</ul>}
    </div>
  </details>;
}

function message(reason: unknown) {
  return typeof reason === 'string' ? reason : 'Could not update the private-server list.';
}
