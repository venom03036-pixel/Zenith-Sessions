import { invoke } from '@tauri-apps/api/core';
import { useEffect, useState } from 'react';

export type Account = { profileId: string; username: string };
type Profile = { userId: string; displayName: string; description: string; created: string };
type Favorite = { placeId: string; name: string };
type Friend = { id: string; name: string; displayName: string };
type Page<T> = { items: T[]; nextCursor: string | null };
type Remote<T> = { value?: T; error?: string };
type Outcome<T> = { Ok: T } | { Err: string };
type PrivateProfile = { balance: Outcome<string>; friends: Outcome<Page<Friend>> };

function errorMessage(error: unknown) {
  return typeof error === 'string' ? error : 'Roblox data is unavailable.';
}

export function Avatar({ username, image, large = false }: { username: string; image?: string; large?: boolean }) {
  const [visible, setVisible] = useState(false);
  return <span className={`avatar preview-avatar${large ? ' inspector-avatar' : ''}`}>
    {!visible && <span>{username.slice(0, 2).toUpperCase()}</span>}
    {image && <img src={image} alt="" referrerPolicy="no-referrer" hidden={!visible}
      onLoad={() => setVisible(true)} onError={() => setVisible(false)}/>}
  </span>;
}

function added<T>(page: Page<T>, next: Page<T>, key: (item: T) => string): Page<T> {
  const items = new Map(page.items.map(item => [key(item), item]));
  next.items.forEach(item => items.set(key(item), item));
  return { items: [...items.values()], nextCursor: next.nextCursor };
}

function dateLabel(date: string) {
  const value = new Date(date);
  return Number.isNaN(value.valueOf()) ? 'Unavailable' : value.toLocaleDateString();
}

export function AccountInspector({ account, image, onFavorite }: {
  account: Account;
  image?: string;
  onFavorite: (favorite: Favorite) => void;
}) {
  const [revision, setRevision] = useState(0);
  return <AccountDetails key={revision} account={account} image={image} onFavorite={onFavorite}
    onRefresh={() => setRevision(value => value + 1)}/>;
}

function AccountDetails({ account, image, onFavorite, onRefresh }: {
  account: Account;
  image?: string;
  onFavorite: (favorite: Favorite) => void;
  onRefresh: () => void;
}) {
  const [profile, setProfile] = useState<Remote<Profile>>({});
  const [favorites, setFavorites] = useState<Remote<Page<Favorite>>>({});
  const [balance, setBalance] = useState<Remote<string>>({});
  const [friends, setFriends] = useState<Remote<Page<Friend>>>({});
  const [moreFavorites, setMoreFavorites] = useState(false);
  const [moreFriends, setMoreFriends] = useState(false);
  const id = account.profileId;

  useEffect(() => {
    let current = true;
    const timer = window.setTimeout(() => {
      void invoke<Profile>('account_profile', { id }).then(
        value => { if (current) setProfile({ value }); },
        error => { if (current) setProfile({ error: errorMessage(error) }); },
      );
      void invoke<Page<Favorite>>('account_favorites', { id, cursor: null }).then(
        value => { if (current) setFavorites({ value }); },
        error => { if (current) setFavorites({ error: errorMessage(error) }); },
      );
      void invoke<PrivateProfile>('account_private_profile', { id }).then(
        value => {
          if (!current) return;
          setBalance('Ok' in value.balance ? { value: value.balance.Ok } : { error: value.balance.Err });
          setFriends('Ok' in value.friends ? { value: value.friends.Ok } : { error: value.friends.Err });
        },
        error => {
          if (!current) return;
          setBalance({ error: errorMessage(error) });
          setFriends({ error: errorMessage(error) });
        },
      );
    }, 150);
    return () => { current = false; window.clearTimeout(timer); };
  }, [id]);

  async function loadMoreFavorites() {
    const page = favorites.value;
    if (!page?.nextCursor || moreFavorites) return;
    setMoreFavorites(true);
    try {
      const next = await invoke<Page<Favorite>>('account_favorites', { id, cursor: page.nextCursor });
      setFavorites({ value: added(page, next, item => item.placeId) });
    } catch (error) { setFavorites({ value: page, error: errorMessage(error) }); }
    finally { setMoreFavorites(false); }
  }

  async function loadMoreFriends() {
    const page = friends.value;
    if (!page?.nextCursor || moreFriends) return;
    setMoreFriends(true);
    try {
      const next = await invoke<Page<Friend>>('account_friends', { id, cursor: page.nextCursor });
      setFriends({ value: added(page, next, item => item.id) });
    } catch (error) { setFriends({ value: page, error: errorMessage(error) }); }
    finally { setMoreFriends(false); }
  }

  return <aside className="account-panel account-inspector" aria-label="Account details">
    <div className="inspector-top">
      <Avatar key={image ?? id} username={account.username} image={image} large/>
      <div><h2>{profile.value?.displayName ?? account.username}</h2><p>@{account.username}</p></div>
      <button className="secondary-button" onClick={onRefresh} aria-label="Refresh account details">Refresh</button>
    </div>
    {profile.value && <>
      <dl><dt>Roblox ID</dt><dd>{profile.value.userId}</dd>
        <dt>Joined</dt><dd>{dateLabel(profile.value.created)}</dd></dl>
      {profile.value.description && <p className="account-bio">{profile.value.description}</p>}
    </>}
    {profile.error && <p role="status">{profile.error}</p>}
    <section>
      <h3>Robux</h3>
      <p>{balance.value !== undefined ? <strong>{BigInt(balance.value).toLocaleString()}</strong>
        : balance.error ? <span role="status">{balance.error}</span> : 'Checking balance…'}</p>
    </section>
    <section>
      <h3>Favorite games</h3>
      {favorites.value ? favorites.value.items.length ? <ul className="directory-results">
        {favorites.value.items.map(item => <li key={item.placeId}>
          <button className="secondary-button" onClick={() => onFavorite(item)}>{item.name}</button>
        </li>)}
      </ul> : <p>No public favorites returned.</p> : !favorites.error && <p>Loading favorites…</p>}
      {favorites.error && <p role="status">{favorites.error}</p>}
      {favorites.value?.nextCursor && <button className="secondary-button" disabled={moreFavorites}
        onClick={() => void loadMoreFavorites()}>{moreFavorites ? 'Loading…' : 'More favorites'}</button>}
    </section>
    <section>
      <h3>Friends</h3>
      {friends.value ? friends.value.items.length ? <ul className="directory-results friends-list">
        {friends.value.items.map(item => <li key={item.id}>
          <strong>{item.displayName || item.name || `Roblox user ${item.id}`}</strong>
          {item.name && <span>@{item.name}</span>}
        </li>)}
      </ul> : <p>No friends returned.</p> : !friends.error && <p>Loading friends…</p>}
      {friends.error && <p role="status">{friends.error}</p>}
      {friends.value?.nextCursor && <button className="secondary-button" disabled={moreFriends}
        onClick={() => void loadMoreFriends()}>{moreFriends ? 'Loading…' : 'More friends'}</button>}
    </section>
  </aside>;
}
