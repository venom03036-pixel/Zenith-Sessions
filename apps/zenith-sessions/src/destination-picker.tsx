import { invoke } from '@tauri-apps/api/core';
import { useEffect, useRef, useState } from 'react';
import { PublicGameDetails } from './public-game-details';
import { RecentServers, type ServerHistory } from './recent-servers';

type Game = { placeId: string; name: string };
type Server = { id: string; playing: number; maxPlayers: number; ping: number | null; fps: number | null };
type ServerPage = { servers: Server[]; previousCursor: string | null; nextCursor: string | null };
type ServerView = { pages: ServerPage[]; index: number };
type PageDirection = 'previous' | 'next';
type ServerOrder = 'least_players' | 'most_players';
type ServerFilters = { order: ServerOrder; includeFull: boolean };
const MAX_CACHED_SERVER_PAGES = 8;

export type Destination = { place: string; name: string; job: string | null };

export function DestinationPicker({ destination, onChange, disabled, autoShowDetails, onAutoShowDetailsChange, serverHistory, onClearServerHistory }: {
  destination: Destination;
  onChange: (destination: Destination) => void;
  disabled: boolean;
  autoShowDetails: boolean;
  onAutoShowDetailsChange: (enabled: boolean) => void;
  serverHistory?: ServerHistory;
  onClearServerHistory: () => Promise<boolean>;
}) {
  const [query, setQuery] = useState('');
  const [games, setGames] = useState<Game[] | null>(null);
  const [serverView, setServerView] = useState<ServerView | null>(null);
  const [filters, setFilters] = useState<ServerFilters>({ order: 'least_players', includeFull: false });
  const [searching, setSearching] = useState(false);
  const [browsing, setBrowsing] = useState(false);
  const [searchError, setSearchError] = useState('');
  const [serverError, setServerError] = useState('');
  const [editingPlace, setEditingPlace] = useState(false);
  const searches = useRef(0);
  const pages = useRef(0);
  const pendingPage = useRef<number | null>(null);
  const servers = serverView?.pages[serverView.index];
  const pinnedServer = servers?.servers.find(server => server.id === destination.job);

  useEffect(() => () => { searches.current++; pages.current++; pendingPage.current = null; }, []);

  function changePlace(place: string, name = '', job: string | null = null) {
    pages.current++;
    setServerView(null);
    setServerError('');
    onChange({ place, name, job });
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

  async function browse(cursor: string | null, queryFilters = filters, direction?: PageDirection) {
    if (pendingPage.current !== null) return;
    const request = ++pages.current;
    pendingPage.current = request;
    setBrowsing(true);
    setServerError('');
    try {
      const result = await invoke<ServerPage>('browse_public_servers', { place: destination.place, cursor, filters: queryFilters });
      if (pages.current === request) {
        setServerView(previous => {
          if (!previous || !direction) return { pages: [result], index: 0 };
          if (direction === 'previous') return { pages: [result, ...previous.pages].slice(0, MAX_CACHED_SERVER_PAGES), index: 0 };
          const pages = [...previous.pages, result].slice(-MAX_CACHED_SERVER_PAGES);
          return { pages, index: pages.length - 1 };
        });
      }
    } catch (error) {
      if (pages.current === request) setServerError(typeof error === 'string' ? error : 'Could not load Roblox servers.');
    } finally {
      if (pendingPage.current === request) {
        pendingPage.current = null;
        setBrowsing(false);
      }
    }
  }

  function changeFilters(next: ServerFilters) {
    if (pendingPage.current !== null) return;
    setFilters(next);
    setServerView(null);
    setServerError('');
    if (servers || serverError) void browse(null, next);
  }

  function step(direction: PageDirection) {
    if (!serverView || !servers || pendingPage.current !== null) return;
    const index = serverView.index + (direction === 'previous' ? -1 : 1);
    if (serverView.pages[index]) {
      setServerError('');
      setServerView({ ...serverView, index });
      return;
    }
    const cursor = direction === 'previous' ? servers.previousCursor : servers.nextCursor;
    if (cursor) void browse(cursor, filters, direction);
  }

  return <div className="destination-picker">
    <RecentServers history={serverHistory} disabled={disabled} onClear={onClearServerHistory}
      onSelect={visit => { setEditingPlace(false); changePlace(visit.placeId, '', visit.serverId); }}/>
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
        onClick={() => { setEditingPlace(false); changePlace(game.placeId, game.name); setGames(null); }}>{game.name}</button></li>)}
    </ul>}
    <div className="destination-input">
      <label>Place ID or public Roblox game link <input value={destination.place}
        onChange={event => { setEditingPlace(true); changePlace(event.target.value); }}
        onBlur={() => setEditingPlace(false)} onKeyDown={event => {
          if (event.key === 'Enter' && !event.nativeEvent.isComposing) setEditingPlace(false);
        }}
        maxLength={512} placeholder="Enter a place ID or game link" disabled={disabled}/></label>
      <button className="secondary-button" disabled={disabled || browsing || !destination.place.trim()} onClick={() => void browse(null)}>
        {browsing ? 'Loading…' : 'Browse public servers'}
      </button>
    </div>
    <div className="server-options row-actions">
      <label>Public server order <select aria-label="Public server order" value={filters.order} disabled={disabled || browsing}
        onChange={event => changeFilters({ ...filters, order: event.target.value as ServerOrder })}>
        <option value="least_players">Fewest players first</option>
        <option value="most_players">Most players first</option>
      </select></label>
      <label className="pair-choice"><input type="checkbox" aria-label="Show full servers" checked={filters.includeFull} disabled={disabled || browsing}
        onChange={event => changeFilters({ ...filters, includeFull: event.target.checked })}/> Show full servers</label>
    </div>
    {destination.name && <p>Game selected · {destination.name}</p>}
    <label className="pair-choice">
      <input type="checkbox" aria-label="Automatically show game details" checked={autoShowDetails}
        onChange={event => onAutoShowDetailsChange(event.target.checked)}/> Automatically show game details
    </label>
    {autoShowDetails && editingPlace && destination.place.trim() &&
      <p className="public-game-note">Press Enter or leave the place field to show game details.</p>}
    {destination.place.trim() && <PublicGameDetails key={`${autoShowDetails}:${destination.place}`} place={destination.place}
      autoLoad={autoShowDetails && !editingPlace} disabled={disabled}/>}
    {destination.job && <div className="row-actions directory-selection">
      <p>Selected server · <code>{destination.job}</code></p>
      <button className="secondary-button" disabled={disabled} onClick={() => onChange({ ...destination, job: null })}>Use any server</button>
    </div>}
    {pinnedServer && pinnedServer.playing >= pinnedServer.maxPlayers &&
      <p role="status">Roblox reported the selected server full on this page. Its server ID is still selected.</p>}
    {serverError && <p role="alert">{serverError}</p>}
    {servers && <>
      <p>Reported occupancy can change. Roblox assigns seats when you join. Ping and server FPS appear when Roblox reports them.</p>
      {servers.servers.length ? <ul className="directory-results" aria-label="Public servers">
        {servers.servers.map(server => <li key={server.id}>
          <button className="secondary-button" disabled={disabled || browsing || server.playing >= server.maxPlayers}
            aria-pressed={destination.job === server.id} onClick={() => onChange({ ...destination, job: server.id })}>
            <code>{server.id}</code><span>{server.playing} / {server.maxPlayers} players
              {server.playing === server.maxPlayers && ' · Full'}{server.ping === null ? '' : ` · ${server.ping} ms`}
              {server.fps === null ? '' : ` · ${server.fps.toLocaleString(undefined, { maximumFractionDigits: 1 })} server FPS`}</span>
          </button>
        </li>)}
      </ul> : <p role="status">{filters.includeFull ? 'Roblox returned no public servers on this page.'
        : 'Roblox returned no public servers with room on this page.'}</p>}
      {serverView && (servers.previousCursor || servers.nextCursor || serverView.pages.length > 1) && <div className="row-actions directory-selection">
        {(servers.previousCursor || serverView.index > 0) && <button className="secondary-button" disabled={disabled || browsing}
          onClick={() => step('previous')}>Previous 25 servers</button>}
        {(servers.nextCursor || serverView.index + 1 < serverView.pages.length) && <button className="secondary-button" disabled={disabled || browsing}
          onClick={() => step('next')}>Next 25 servers</button>}
      </div>}
    </>}
  </div>;
}
