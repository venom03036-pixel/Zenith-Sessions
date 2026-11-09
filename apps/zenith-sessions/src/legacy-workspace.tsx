export type LegacyProfile = {
  id: string; label: string; username: string; groups: string[]; priority: number;
  preferredPlaceId: string | null; legacyUserId: string | null; notes: string; isFavorite: boolean;
};
type Favorite = { id: string; name: string; placeId: string; jobId: string | null };
export type LegacyWorkspace = {
  protection: 'windows' | 'password-protected' | 'unavailable';
  profileCount: number; groupCount: number; bundleCount: number; assignmentCount: number;
  archivedCount: number; archived: LegacyProfile[]; linked: Record<string, LegacyProfile>;
  favorites: Favorite[]; theme: string | null;
};

export function LegacyProfileDetails({ profile }: { profile: LegacyProfile }) {
  return <>
    <dl>
      <dt>Saved label</dt><dd>{profile.label}{profile.isFavorite ? ' · Favorite' : ''}</dd>
      <dt>Groups</dt><dd>{profile.groups.join(', ')}</dd>
      <dt>Priority</dt><dd>{profile.priority}</dd>
      {profile.preferredPlaceId && <><dt>Preferred place</dt><dd>{profile.preferredPlaceId}</dd></>}
    </dl>
    {profile.notes && <p className="account-bio">{profile.notes}</p>}
  </>;
}

export function LegacyWorkspacePanel({ workspace, offset, onPage, onFavorite, onTheme }: {
  workspace: LegacyWorkspace;
  offset: number;
  onPage: (offset: number) => void;
  onFavorite: (favorite: Favorite) => void;
  onTheme?: () => void;
}) {
  return <section className="account-panel preview-panel legacy-workspace">
    <h2>Imported legacy workspace</h2>
    <p>{workspace.profileCount} profiles · {workspace.groupCount} groups · {workspace.bundleCount} bundles · {workspace.assignmentCount} macro assignments preserved</p>
    {workspace.protection === 'password-protected' && <p>Password-protected sign-ins remain in the original vault.</p>}
    {workspace.protection === 'unavailable' && <p>The original vault could not be opened. Profile metadata was preserved.</p>}
    {onTheme && <button className="secondary-button" onClick={onTheme}>Apply imported theme</button>}
    {workspace.favorites.length > 0 && <details>
      <summary>Legacy favorite destinations ({workspace.favorites.length})</summary>
      <ul className="directory-results">
        {workspace.favorites.map(item => <li key={item.id}>
          <button className="secondary-button" onClick={() => onFavorite(item)}>{item.name}</button>
        </li>)}
      </ul>
    </details>}
    {workspace.archivedCount > 0 && <details open>
      <summary>Archived profiles ({workspace.archivedCount})</summary>
      <p>These profiles are preserved as metadata. A matching verified sign-in is required to use them for launches.</p>
      {workspace.archived.map(profile => <details key={profile.id} className="archived-profile">
        <summary>{profile.label}{profile.username ? ` · @${profile.username}` : ''}</summary>
        <LegacyProfileDetails profile={profile}/>
      </details>)}
      <div className="row-actions">
        {offset > 0 && <button className="secondary-button" onClick={() => onPage(Math.max(0, offset - 50))}>Previous profiles</button>}
        {offset + workspace.archived.length < workspace.archivedCount
          && <button className="secondary-button" onClick={() => onPage(offset + 50)}>More profiles</button>}
      </div>
    </details>}
  </section>;
}
