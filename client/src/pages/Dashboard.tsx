import { useCallback, useEffect, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { api, Environment, JobSummary } from '../api';
import { useJobProgress } from '../signalr';
import { StatusBadge, duration, fmtDate } from '../components/shared';

export default function Dashboard() {
  const [envs, setEnvs] = useState<Environment[]>([]);
  const [jobs, setJobs] = useState<JobSummary[]>([]);
  const navigate = useNavigate();

  useEffect(() => {
    api.environments.list().then(setEnvs).catch(console.error);
    api.jobs.list().then(setJobs).catch(console.error);
  }, []);

  // live-update the job list as progress events arrive
  const onProgress = useCallback((job: JobSummary) => {
    setJobs(prev => {
      const idx = prev.findIndex(j => j.id === job.id);
      if (idx === -1) return [job, ...prev];
      const next = [...prev];
      next[idx] = job;
      return next;
    });
  }, []);
  useJobProgress(onProgress);

  const active = jobs.filter(j => j.status === 'Running' || j.status === 'Applying');
  const awaiting = jobs.filter(j => j.status === 'AwaitingReview');
  const completed = jobs.filter(j => j.status === 'Completed');

  return (
    <>
      <h1>Dashboard</h1>
      <p className="subtitle">Synchronise content between your Sitecore environments.</p>

      <div className="grid cols-4">
        <div className="card stat">
          <div className="label">Environments</div>
          <div className="value">{envs.length}</div>
        </div>
        <div className="card stat">
          <div className="label">Active syncs</div>
          <div className="value" style={{ color: active.length ? 'var(--accent)' : undefined }}>
            {active.length}
          </div>
        </div>
        <div className="card stat">
          <div className="label">Awaiting review</div>
          <div className="value" style={{ color: awaiting.length ? 'var(--amber)' : undefined }}>
            {awaiting.length}
          </div>
        </div>
        <div className="card stat">
          <div className="label">Completed syncs</div>
          <div className="value">{completed.length}</div>
        </div>
      </div>

      <div className="toolbar" style={{ marginTop: 26 }}>
        <h2 style={{ margin: 0 }}>Environments</h2>
        <Link to="/environments"><button className="secondary small">Manage</button></Link>
      </div>
      <div className="grid cols-2" style={{ marginTop: 12 }}>
        {envs.map(e => (
          <div key={e.id} className="card">
            <div className="env-pill">
              <span className="dot" style={{ background: e.color }} />
              {e.name}
              <span className="spacer" />
              <span className={`badge ${e.connectorType === 'Simulated' ? 'purple' : 'blue'}`}>
                {e.connectorType === 'Simulated' ? 'Simulated' : 'Item Service'}
              </span>
            </div>
            <div className="dim mono" style={{ marginTop: 8, fontSize: 12 }}>{e.baseUrl}</div>
            <div className="row" style={{ marginTop: 10 }}>
              {e.databases.map(db => <span key={db} className="badge gray">{db}</span>)}
            </div>
          </div>
        ))}
        {envs.length === 0 && (
          <div className="card dim">No environments yet — add one under Environments.</div>
        )}
      </div>

      <div className="toolbar" style={{ marginTop: 26 }}>
        <h2 style={{ margin: 0 }}>Sync history</h2>
        <Link to="/sync/new"><button className="small">+ New sync</button></Link>
      </div>
      <div className="card" style={{ marginTop: 12, padding: 0 }}>
        <table>
          <thead>
            <tr>
              <th>Direction</th><th>Root</th><th>Status</th><th>Changes</th>
              <th>Errors</th><th>Started</th><th>Duration</th>
            </tr>
          </thead>
          <tbody>
            {jobs.map(j => (
              <tr key={j.id} className="clickable" onClick={() => navigate(`/jobs/${j.id}`)}>
                <td>
                  <strong>{j.sourceEnvironmentName}</strong> <span className="dim">({j.sourceDatabase})</span>
                  {' → '}
                  <strong>{j.targetEnvironmentName}</strong> <span className="dim">({j.targetDatabase})</span>
                </td>
                <td className="mono dim">{j.rootPath}</td>
                <td><StatusBadge status={j.status} /></td>
                <td>
                  <span style={{ color: 'var(--green)' }}>+{j.counters.new}</span>{' '}
                  <span style={{ color: 'var(--amber)' }}>~{j.counters.modified}</span>{' '}
                  <span style={{ color: 'var(--red)' }}>−{j.counters.deleted}</span>
                  {j.counters.conflicts > 0 && (
                    <span style={{ color: 'var(--purple)' }}> ⚠{j.counters.conflicts}</span>
                  )}
                </td>
                <td>{j.counters.errors > 0 ? <span style={{ color: 'var(--red)' }}>{j.counters.errors}</span> : '—'}</td>
                <td className="dim">{fmtDate(j.started)}</td>
                <td className="dim">{duration(j)}</td>
              </tr>
            ))}
            {jobs.length === 0 && (
              <tr><td colSpan={7} className="dim">No syncs yet. Start one with “New sync”.</td></tr>
            )}
          </tbody>
        </table>
      </div>
    </>
  );
}
