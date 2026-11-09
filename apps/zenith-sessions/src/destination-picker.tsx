import { invoke } from '@tauri-apps/api/core';
import { useEffect, useRef, useState } from 'react';
import { PublicGameDetails } from './public-game-details';

type Game = { placeId: string; name: string };
type Server = { id: string; playing: number; maxPlayers: number; ping: number | null };
type ServerPage = { servers: Server[]; nextCursor: string | null };

export type Destination = { place: string; name: string; job: string | null };

export function DestinationPicker({ destination, onChange, disabled }: {
  destination: Destination;
  onChange: (destination: Destination) => void;
  disabled: boolean;
}) {
  const [query, setQuery] = useState('');
  const [games, setGames] = useState<Game[] | null>(null);
  const [servers, setServers] = useState<ServerPage | null>(null);
  const [searching, setSearching] = useState(false);
  const [browsing, setBrowsing] = useState(false);
  const [searchError, setSearchError] = useState('');
  const [serverError, setServerError] = useState('');
  const searches = useRef(0);
  const pages = useRef(0);

  useEffect(() => () => { searches.current++; pages.current++; }, []);

  function changePlace(place: string, name = '') {
    pages.current++;
    setServers(null);
    setBrowsing(false);
    setServerError('');
    onChange({ place, name, job: null });
  }

  async function search() {
    const request = ++searches.current;
    setSearching(true);
    setGames(null);
    setSearchError('');
    try {
      const result = await invoke<Game[]>('search_public_games', { query });
      if (searches.current === request) setGames(result);
    } catch (error) {
      if (searches.current === request) setSearchError(typeof error === 'string' ? error : 'Could not search Roblox games.');
    } finally {
      if (searches.current === request) setSearching(false);
    }
  }

  async function browse(cursor: string | null) {
    const request = ++pages.current;
    setBrowsing(true);
    setServerError('');
    try {
      const result = await invoke<ServerPage>('browse_public_servers', { place: destination.place, cursor });
      if (pages.current === request) setServers(result);
    } catch (error) {
      if (pages.current === request) setServerError(typeof error === 'string' ? error : 'Could not load Roblox servers.');
    } finally {
      if (pages.current === request) setBrowsing(false);
    }
  }

  return <div className="destination-picker">
    <form className="destination-input" onSubmit={event => { event.preventDefault(); void search(); }}>
      <label>Find a game by name <input value={query} onChange={event => {
        searches.current++;
        setQuery(event.target.value);
        setGames(null);
        setSearchError('');
        setSearching(false);
      }} placeholder="Search Roblox games" maxLength={100} disabled={disabled}/></label>
      <button className="secondary-button" disabled={disabled || searching || !query.trim()}>{searching ? 'Searching…' : 'Search'}</button>
    </form>
    {searchError && <p role="alert">{searchError}</p>}
    {games?.length === 0 && <p role="status">No public games matched that name.</p>}
    {!!games?.length && <ul className="directory-results" aria-label="Game results">
      {games.map(game => <li key={game.placeId}><button className="secondary-button" disabled={disabled}
        onClick={() => { changePlace(game.placeId, game.name); setGames(null); }}>{game.name}</button></li>)}
    </ul>}
    <div className="destination-input">
      <label>Place ID or public Roblox game link <input value={destination.place} onChange={event => changePlace(event.target.value)}
        maxLength={512} placeholder="Enter a place ID or game link" disabled={disabled}/></label>
      <button className="secondary-button" disabled={disabled || browsing || !destination.place.trim()} onClick={() => void browse(null)}>
        {browsing ? 'Loading…' : 'Browse public servers'}
      </button>
    </div>
    {destination.name && <p>Game selected · {destination.name}</p>}
    {destination.place.trim() && <PublicGameDetails key={destination.place} place={destination.place} disabled={disabled}/>}
    {destination.job && <div className="row-actions directory-selection">
      <p>Selected server · <code>{destination.job}</code></p>
      <button className="secondary-button" disabled={disabled} onClick={() => onChange({ ...destination, job: null })}>Use any server</button>
    </div>}
    {serverError && <p role="alert">{serverError}</p>}
    {servers && <>
      <p>Reported occupancy can change. Roblox assigns seats when you join.</p>
      {servers.servers.length ? <ul className="directory-results" aria-label="Public servers">
        {servers.servers.map(server => <li key={server.id}>
          <button className="secondary-button" disabled={disabled || server.playing >= server.maxPlayers}
            aria-pressed={destination.job === server.id} onClick={() => onChange({ ...destination, job: server.id })}>
            <code>{server.id}</code><span>{server.playing} / {server.maxPlayers} players{server.ping === null ? '' : ` · ${server.ping} ms`}</span>
          </button>
        </li>)}
      </ul> : <p role="status">Roblox returned no public servers with room on this page.</p>}
      {servers.nextCursor && <button className="secondary-button" disabled={disabled || browsing}
        onClick={() => void browse(servers.nextCursor)}>Next 25 servers</button>}
    </>}
  </div>;
}
