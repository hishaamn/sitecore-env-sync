import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useParams } from 'react-router-dom';
import {
  api, ConflictResolution, DiffStatus, ItemDiff, JobSummary, LogEntry
} from '../api';
import { useJobLog, useJobProgress } from '../signalr';
import { PhaseStepper, ProgressBar, StatusBadge, duration, fmtDate } from '../components/shared';

const STATUS_META: Record<DiffStatus, { label: string; color: string; sign: string }> = {
  New: { label: 'New', color: 'var(--green)', sign: '+' },
  Modified: { label: 'Modified', color: 'var(--amber)', sign: '~' },
  Deleted: { label: 'Only on target', color: 'var(--red)', sign: '−' },
  Conflict: { label: 'Conflict', color: 'var(--purple)', sign: '⚠' },
  Unchanged: { label: 'Unchanged', color: 'var(--text-dim)', sign: '=' }
};

export default function JobDetail() {
  const { jobId = '' } = useParams();
  const [job, setJob] = useState<JobSummary | null>(null);
  const [diffs, setDiffs] = useState<ItemDiff[]>([]);
  const [log, setLog] = useState<LogEntry[]>([]);
  const [filter, setFilter] = useState<DiffStatus | 'All'>('All');
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [resolutions, setResolutions] = useState<Record<string, ConflictResolution>>({});
  const [error, setError] = useState('');
  const logRef = useRef<HTMLDivElement>(null);
  const lastStatus = useRef<string>('');

  const loadDiff = useCallback(() => {
    api.jobs.diff(jobId).then(setDiffs).catch(() => {});
  }, [jobId]);

  useEffect(() => {
    api.jobs.get(jobId).then(j => {
      setJob(j);
      lastStatus.current = j.status;
      if (j.status !== 'Running' && j.status !== 'Queued') loadDiff();
    }).catch(e => setError(e.message));
    api.jobs.log(jobId).then(setLog).catch(() => {});
  }, [jobId, loadDiff]);

  const onProgress = useCallback((j: JobSummary) => {
    if (j.id !== jobId) return;
    setJob(j);
    // refresh the diff list whenever a phase that changes it finishes
    if (j.status !== lastStatus.current &&
        (j.status === 'AwaitingReview' || j.status === 'Completed' || j.status === 'Failed')) {
      loadDiff();
    }
    lastStatus.current = j.status;
  }, [jobId, loadDiff]);
  useJobProgress(onProgress);

  const onLog = useCallback((payload: { jobId: string; entry: LogEntry }) => {
    if (payload.jobId !== jobId) return;
    setLog(prev => [...prev, payload.entry]);
  }, [jobId]);
  useJobLog(onLog);

  useEffect(() => {
    logRef.current?.scrollTo({ top: logRef.current.scrollHeight });
  }, [log]);

  const conflicts = useMemo(() => diffs.filter(d => d.status === 'Conflict'), [diffs]);
  const unresolved = conflicts.filter(d => !resolutions[d.itemId] || resolutions[d.itemId] === 'Unresolved');
  const changes = useMemo(() => diffs.filter(d => d.status !== 'Unchanged'), [diffs]);
  const visible = filter === 'All' ? changes : changes.filter(d => d.status === filter);

  const counts = useMemo(() => {
    const c: Record<string, number> = { All: changes.length };
    for (const d of changes) c[d.status] = (c[d.status] ?? 0) + 1;
    return c;
  }, [changes]);

  const toggle = (id: string) => setExpanded(prev => {
    const next = new Set(prev);
    next.has(id) ? next.delete(id) : next.add(id);
    return next;
  });

  const resolve = (id: string, res: ConflictResolution) =>
    setResolutions(prev => ({ ...prev, [id]: res }));

  const resolveAll = (res: ConflictResolution) =>
    setResolutions(prev => {
      const next = { ...prev };
      for (const c of conflicts) next[c.itemId] = res;
      return next;
    });

  const apply = async () => {
    setError('');
    try {
      const j = await api.sync.apply(jobId, resolutions);
      setJob(j);
      lastStatus.current = j.status;
    } catch (e: any) {
      setError(e.message);
    }
  };

  const cancel = async () => {
    try { await api.sync.cancel(jobId); } catch { /* already done */ }
  };

  if (!job) return <p className="dim">{error || 'Loading…'}</p>;

  const running = job.status === 'Running' || job.status === 'Applying';
  const appliable = changes.filter(d =>
    d.status !== 'Deleted' || true // server enforces orphan-deletion scope; show count of all changes
  ).length;

  return (
    <>
      <div className="toolbar">
        <div>
          <h1>
            {job.sourceEnvironmentName} <span className="dim">({job.sourceDatabase})</span>
            {' → '}
            {job.targetEnvironmentName} <span className="dim">({job.targetDatabase})</span>
          </h1>
          <p className="subtitle" style={{ marginBottom: 0 }}>
            <span className="mono">{job.rootPath}</span> · started {fmtDate(job.started)} · {duration(job)}
            {' · '}job <span className="mono">{job.id}</span>
          </p>
        </div>
        <div className="row">
          <StatusBadge status={job.status} />
          {running && <button className="danger small" onClick={cancel}>Cancel</button>}
        </div>
      </div>

      {(error || job.error) && <div className="error-banner">{error || job.error}</div>}

      <div className="card" style={{ marginTop: 18 }}>
        <PhaseStepper phase={job.phase} status={job.status} />
        <ProgressBar job={job} />
        <div className="grid cols-4" style={{ marginTop: 16 }}>
          <Counter label="Source items" value={job.counters.sourceItems} />
          <Counter label="New" value={job.counters.new} color="var(--green)" />
          <Counter label="Modified" value={job.counters.modified} color="var(--amber)" />
          <Counter label="Only on target" value={job.counters.deleted} color="var(--red)" />
          <Counter label="Conflicts" value={job.counters.conflicts} color="var(--purple)" />
          <Counter label="Unchanged" value={job.counters.unchanged} />
          <Counter label="Applied" value={job.counters.applied} color="var(--accent)" />
          <Counter label="Errors" value={job.counters.errors} color={job.counters.errors ? 'var(--red)' : undefined} />
        </div>
      </div>

      {job.status === 'AwaitingReview' && (
        <div className="card" style={{ marginTop: 16, borderColor: 'var(--amber)' }}>
          <div className="toolbar">
            <div>
              <strong>Review &amp; apply</strong>
              <div className="dim" style={{ fontSize: 12.5, marginTop: 4 }}>
                {changes.length} change(s) will be considered.{' '}
                {conflicts.length > 0 && (
                  unresolved.length > 0
                    ? `${unresolved.length} of ${conflicts.length} conflict(s) still unresolved — unresolved conflicts are skipped.`
                    : 'All conflicts resolved.'
                )}
              </div>
            </div>
            <div className="row">
              {conflicts.length > 0 && (
                <>
                  <button className="secondary small" onClick={() => resolveAll('UseSource')}>
                    Resolve all: use source
                  </button>
                  <button className="ghost small" onClick={() => resolveAll('Skip')}>
                    Resolve all: skip
                  </button>
                </>
              )}
              <button onClick={apply} disabled={changes.length === 0}>
                Apply {appliable} change(s) →
              </button>
            </div>
          </div>
        </div>
      )}

      <h2>Activity log</h2>
      <div className="log" ref={logRef}>
        {log.map((l, i) => (
          <div key={i}>
            <span className="t">{new Date(l.time).toLocaleTimeString()}</span>
            <span className={l.level}>{l.message}</span>
          </div>
        ))}
        {log.length === 0 && <span className="dim">Waiting for output…</span>}
      </div>

      {diffs.length > 0 && (
        <>
          <h2>Changes</h2>
          <div className="filter-tabs">
            {(['All', 'New', 'Modified', 'Deleted', 'Conflict'] as const).map(f => (
              <button key={f} className={`small ${filter === f ? 'on' : ''}`} onClick={() => setFilter(f)}>
                {f === 'All' ? 'All changes' : STATUS_META[f as DiffStatus].label} ({counts[f] ?? 0})
              </button>
            ))}
          </div>
          <div className="card" style={{ padding: 0 }}>
            <table>
              <thead>
                <tr>
                  <th style={{ width: 110 }}>Status</th>
                  <th>Item</th>
                  <th style={{ width: 120 }}>Template</th>
                  <th style={{ width: 110 }}>Fields</th>
                  {job.status === 'AwaitingReview' && <th style={{ width: 220 }}>Resolution</th>}
                </tr>
              </thead>
              <tbody>
                {visible.map(d => {
                  const meta = STATUS_META[d.status];
                  const isOpen = expanded.has(d.itemId);
                  const res = resolutions[d.itemId] ?? d.resolution;
                  return [
                    <tr key={d.itemId}>
                      <td><span style={{ color: meta.color, fontWeight: 600 }}>{meta.sign} {meta.label}</span></td>
                      <td>
                        <div className="mono">{d.path}</div>
                        {d.status === 'Conflict' && (
                          <div className="dim" style={{ fontSize: 11.5, marginTop: 3 }}>
                            source edited {fmtDate(d.sourceUpdated)} · target edited {fmtDate(d.targetUpdated)}
                          </div>
                        )}
                      </td>
                      <td className="dim">{d.isMedia ? '🖼 ' : ''}{d.templateName}</td>
                      <td>
                        {d.fieldDiffs.length > 0 ? (
                          <button className="expander" onClick={() => toggle(d.itemId)}>
                            {d.fieldDiffs.length} field(s) {isOpen ? '▾' : '▸'}
                          </button>
                        ) : <span className="dim">—</span>}
                      </td>
                      {job.status === 'AwaitingReview' && (
                        <td>
                          {d.status === 'Conflict' ? (
                            <div className="row" style={{ gap: 6 }}>
                              <button
                                className={`small ${res === 'UseSource' ? '' : 'ghost'}`}
                                onClick={() => resolve(d.itemId, 'UseSource')}>
                                Use source
                              </button>
                              <button
                                className={`small ${res === 'Skip' ? 'secondary' : 'ghost'}`}
                                onClick={() => resolve(d.itemId, 'Skip')}>
                                Skip
                              </button>
                            </div>
                          ) : (
                            <button
                              className={`small ${res === 'Skip' ? 'secondary' : 'ghost'}`}
                              onClick={() => resolve(d.itemId, res === 'Skip' ? 'Unresolved' : 'Skip')}>
                              {res === 'Skip' ? 'Skipped ✓' : 'Skip'}
                            </button>
                          )}
                        </td>
                      )}
                    </tr>,
                    isOpen && (
                      <tr key={d.itemId + '-fields'}>
                        <td colSpan={job.status === 'AwaitingReview' ? 5 : 4} style={{ paddingTop: 0 }}>
                          <div className="diff-row-fields">
                            {d.fieldDiffs.map(f => (
                              <div className="field-diff" key={f.fieldName}>
                                <div className="fname">{f.fieldName}</div>
                                <div className="val src">
                                  <span className="side">source</span>
                                  {f.sourceValue ?? <em className="dim">(empty)</em>}
                                </div>
                                <div className="val tgt">
                                  <span className="side">target</span>
                                  {f.targetValue ?? <em className="dim">(empty)</em>}
                                </div>
                              </div>
                            ))}
                          </div>
                        </td>
                      </tr>
                    )
                  ];
                })}
                {visible.length === 0 && (
                  <tr><td colSpan={5} className="dim">Nothing in this category.</td></tr>
                )}
              </tbody>
            </table>
          </div>
        </>
      )}
    </>
  );
}

function Counter({ label, value, color }: { label: string; value: number; color?: string }) {
  return (
    <div className="stat">
      <div className="label">{label}</div>
      <div className="value" style={{ color, fontSize: 21 }}>{value.toLocaleString()}</div>
    </div>
  );
}
