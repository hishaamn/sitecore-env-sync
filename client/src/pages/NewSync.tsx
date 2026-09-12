import { useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { api, Environment } from '../api';

export default function NewSync() {
  const [envs, setEnvs] = useState<Environment[]>([]);
  const [sourceEnvId, setSourceEnvId] = useState('');
  const [sourceDb, setSourceDb] = useState('master');
  const [targetEnvId, setTargetEnvId] = useState('');
  const [targetDb, setTargetDb] = useState('web');
  const [rootPath, setRootPath] = useState('/sitecore/content');
  const [content, setContent] = useState(true);
  const [media, setMedia] = useState(false);
  const [templates, setTemplates] = useState(false);
  const [layout, setLayout] = useState(false);
  const [deleteOrphans, setDeleteOrphans] = useState(false);
  const [error, setError] = useState('');
  const [starting, setStarting] = useState(false);
  const navigate = useNavigate();

  useEffect(() => {
    api.environments.list().then(list => {
      setEnvs(list);
      if (list.length > 0) setSourceEnvId(list[0].id);
      if (list.length > 1) setTargetEnvId(list[1].id);
      else if (list.length === 1) setTargetEnvId(list[0].id);
    }).catch(e => setError(e.message));
  }, []);

  const sourceEnv = useMemo(() => envs.find(e => e.id === sourceEnvId), [envs, sourceEnvId]);
  const targetEnv = useMemo(() => envs.find(e => e.id === targetEnvId), [envs, targetEnvId]);

  // keep the selected database valid for the chosen environment
  useEffect(() => {
    if (sourceEnv && !sourceEnv.databases.includes(sourceDb)) setSourceDb(sourceEnv.databases[0] ?? 'master');
  }, [sourceEnv]);
  useEffect(() => {
    if (targetEnv && !targetEnv.databases.includes(targetDb)) setTargetDb(targetEnv.databases[0] ?? 'web');
  }, [targetEnv]);

  const samePair = sourceEnvId === targetEnvId && sourceDb === targetDb;
  const nothingSelected = !content && !media && !templates && !layout;

  const start = async () => {
    setError('');
    setStarting(true);
    try {
      const job = await api.sync.preview({
        sourceEnvironmentId: sourceEnvId,
        sourceDatabase: sourceDb,
        targetEnvironmentId: targetEnvId,
        targetDatabase: targetDb,
        scope: { content, media, templates, layout, rootPath, deleteOrphans }
      });
      navigate(`/jobs/${job.id}`);
    } catch (e: any) {
      setError(e.message);
      setStarting(false);
    }
  };

  const EnvSelect = (props: {
    label: string; envId: string; db: string;
    onEnv: (v: string) => void; onDb: (v: string) => void; env?: Environment;
  }) => (
    <div className="card">
      <h2 style={{ marginTop: 0 }}>{props.label}</h2>
      <label className="field">
        <span className="name">Environment</span>
        <select value={props.envId} onChange={e => props.onEnv(e.target.value)}>
          {envs.map(e => <option key={e.id} value={e.id}>{e.name}</option>)}
        </select>
      </label>
      <label className="field" style={{ marginBottom: 0 }}>
        <span className="name">Database</span>
        <select value={props.db} onChange={e => props.onDb(e.target.value)}>
          {(props.env?.databases ?? ['master', 'web']).map(db => <option key={db} value={db}>{db}</option>)}
        </select>
      </label>
    </div>
  );

  return (
    <>
      <h1>New synchronisation</h1>
      <p className="subtitle">
        Content flows one way: source → target. You will review every change before anything is written.
      </p>

      {error && <div className="error-banner">{error}</div>}

      <div className="sync-pair">
        <EnvSelect label="Source" envId={sourceEnvId} db={sourceDb}
          onEnv={setSourceEnvId} onDb={setSourceDb} env={sourceEnv} />
        <div className="arrow">→</div>
        <EnvSelect label="Target" envId={targetEnvId} db={targetDb}
          onEnv={setTargetEnvId} onDb={setTargetDb} env={targetEnv} />
      </div>

      {samePair && (
        <div className="error-banner">Source and target are the same environment and database — pick a different combination (e.g. master → web, or another environment).</div>
      )}

      <div className="card" style={{ marginTop: 16 }}>
        <h2 style={{ marginTop: 0 }}>Scope</h2>
        <label className="check">
          <input type="checkbox" checked={content} onChange={e => setContent(e.target.checked)} />
          <span>
            Content items
            <span className="hint">Pages and data items under the root path below, compared field by field.</span>
          </span>
        </label>
        {content && (
          <label className="field" style={{ marginLeft: 26, maxWidth: 420 }}>
            <span className="name">Content root path</span>
            <input className="mono" value={rootPath} onChange={e => setRootPath(e.target.value)} />
          </label>
        )}
        <label className="check">
          <input type="checkbox" checked={media} onChange={e => setMedia(e.target.checked)} />
          <span>
            Media library
            <span className="hint">Media items including binary blobs.</span>
          </span>
        </label>
        <label className="check">
          <input type="checkbox" checked={templates} onChange={e => setTemplates(e.target.checked)} />
          <span>
            Templates
            <span className="hint">/sitecore/templates — schema changes; sync these before content that depends on them.</span>
          </span>
        </label>
        <label className="check">
          <input type="checkbox" checked={layout} onChange={e => setLayout(e.target.checked)} />
          <span>
            Layout &amp; renderings
            <span className="hint">/sitecore/layout — rendering and layout definition items.</span>
          </span>
        </label>
        <hr style={{ border: 'none', borderTop: `1px solid var(--border)`, margin: '14px 0' }} />
        <label className="check">
          <input type="checkbox" checked={deleteOrphans} onChange={e => setDeleteOrphans(e.target.checked)} />
          <span>
            Delete orphaned items on target
            <span className="hint">Items that exist on the target but not on the source (within scope) will be deleted on apply.</span>
          </span>
        </label>
      </div>

      <div className="row" style={{ marginTop: 18 }}>
        <button onClick={start} disabled={starting || samePair || nothingSelected || !sourceEnvId || !targetEnvId}>
          {starting ? 'Starting preview…' : 'Start preview (dry run)'}
        </button>
        <span className="dim">No changes are written during the preview.</span>
      </div>
    </>
  );
}
