import { invoke } from '@tauri-apps/api/core';
import { useEffect, useState } from 'react';

type GameDetails = {
  requestedPlaceId: string;
  rootPlaceId: string;
  universeId: string;
  name: string;
  description: string;
  creator: string;
  playing: string;
  visits: string | null;
  maxPlayers: number;
};
type Outcome<T> = { Ok: T } | { Err: string };
type GameArt = { icon: Outcome<string | null>; thumbnails: Outcome<string[]> };

function GameImage({ source, label, className }: { source: string; label: string; className?: string }) {
  const [failed, setFailed] = useState(false);
  return failed ? <span className={className}>Image unavailable</span>
    : <img className={className} src={source} alt={label} referrerPolicy="no-referrer" onError={() => setFailed(true)}/>;
}

export function PublicGameDetails({ place, disabled }: { place: string; disabled: boolean }) {
  const [request, setRequest] = useState(0);
  const [details, setDetails] = useState<GameDetails | null>(null);
  const [art, setArt] = useState<GameArt | null>(null);
  const [loading, setLoading] = useState(false);
  const [detailError, setDetailError] = useState('');
  const [artError, setArtError] = useState('');

  useEffect(() => {
    if (!request) return;
    let current = true;
    async function load() {
      try {
        const game = await invoke<GameDetails>('public_game_details', { place });
        if (!current) return;
        setDetails(game);
        try {
          const images = await invoke<GameArt>('public_game_art', { universe: game.universeId });
          if (current) setArt(images);
        } catch (error) {
          if (current) setArtError(typeof error === 'string' ? error : 'Could not load game artwork.');
        }
      } catch (error) {
        if (current) setDetailError(typeof error === 'string' ? error : 'Could not load public game details.');
      } finally {
        if (current) setLoading(false);
      }
    }
    void load();
    return () => { current = false; };
  }, [place, request]);

  function refresh() {
    setLoading(true);
    setDetails(null);
    setArt(null);
    setDetailError('');
    setArtError('');
    setRequest(value => value + 1);
  }

  const icon = art && 'Ok' in art.icon ? art.icon.Ok : null;
  const thumbnails = art && 'Ok' in art.thumbnails ? art.thumbnails.Ok : [];
  const artworkErrors = new Set([
    art && 'Err' in art.icon ? art.icon.Err : '',
    art && 'Err' in art.thumbnails ? art.thumbnails.Err : '',
    artError,
  ].filter(Boolean));
  return <section className="public-game-details" aria-label="Public game details">
    <div className="row-actions">
      <button className="secondary-button" type="button" onClick={refresh} disabled={disabled || loading}>
        {!loading ? details ? 'Refresh game details' : 'Load game details'
          : details ? 'Loading artwork…' : 'Loading game details…'}
      </button>
    </div>
    {detailError && <p role="status">{detailError}</p>}
    {details && <>
      <div className="public-game-heading">
        {icon && <GameImage source={icon} label="" className="public-game-icon"/>}
        <div><h3>{details.name}</h3><p>By {details.creator}</p></div>
      </div>
      <dl>
        <dt>Requested place</dt><dd>{details.requestedPlaceId}</dd>
        <dt>Experience root place</dt><dd>{details.rootPlaceId}</dd>
        <dt>Experience ID</dt><dd>{details.universeId}</dd>
        <dt>Players across the experience</dt><dd>{BigInt(details.playing).toLocaleString()}</dd>
        <dt>Listed server limit</dt><dd>{details.maxPlayers.toLocaleString()}</dd>
        {details.visits != null && <><dt>Visits</dt><dd>{BigInt(details.visits).toLocaleString()}</dd></>}
      </dl>
      <p className="public-game-note">Public listing data. Browse servers to see reported occupancy for the requested place.</p>
      {details.description && <details><summary>Description</summary><p className="public-game-description">{details.description}</p></details>}
      {thumbnails.length > 0 && <div className="public-game-gallery" aria-label="Experience thumbnails">
        {thumbnails.map((source, index) => <GameImage key={source} source={source} label={`Experience thumbnail ${index + 1}`}/>)}
      </div>}
      {art && !icon && thumbnails.length === 0 && artworkErrors.size === 0 && <p role="status">No public artwork is available.</p>}
      {[...artworkErrors].map(error => <p key={error} role="status">{error}</p>)}
    </>}
  </section>;
}
