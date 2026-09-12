import type { JobStatus, JobSummary, SyncPhase } from '../api';

export function StatusBadge({ status }: { status: JobStatus }) {
  const map: Record<JobStatus, { cls: string; label: string; live?: boolean }> = {
    Queued: { cls: 'gray', label: 'Queued' },
    Running: { cls: 'blue', label: 'Previewing', live: true },
    AwaitingReview: { cls: 'amber', label: 'Awaiting review' },
    Applying: { cls: 'purple', label: 'Applying', live: true },
    Completed: { cls: 'green', label: 'Completed' },
    Failed: { cls: 'red', label: 'Failed' },
    Cancelled: { cls: 'gray', label: 'Cancelled' }
  };
  const m = map[status] ?? { cls: 'gray', label: status };
  return (
    <span className={`badge ${m.cls}`}>
      {m.live && <span className="dot pulse" style={{ background: 'currentColor' }} />}
      {m.label}
    </span>
  );
}

export const PHASES: { key: SyncPhase; label: string }[] = [
  { key: 'Connecting', label: 'Connect' },
  { key: 'DiscoveringSource', label: 'Read source' },
  { key: 'DiscoveringTarget', label: 'Read target' },
  { key: 'Comparing', label: 'Compare' },
  { key: 'Applying', label: 'Apply' },
  { key: 'Snapshotting', label: 'Snapshot' }
];

export function PhaseStepper({ phase, status }: { phase: SyncPhase; status: JobStatus }) {
  // During preview the apply/snapshot steps are hidden.
  const visible =
    status === 'Running' || (status === 'AwaitingReview' && phase !== 'Applying')
      ? PHASES.slice(0, 4)
      : PHASES;
  const currentIdx = visible.findIndex(p => p.key === phase);
  const allDone = phase === 'Done' || status === 'Completed' || status === 'AwaitingReview';
  return (
    <div className="stepper">
      {visible.map((p, i) => {
        const done = allDone || (currentIdx >= 0 && i < currentIdx);
        const active = !allDone && i === currentIdx;
        return (
          <div key={p.key} className={`step ${done ? 'done' : ''} ${active ? 'active' : ''}`}>
            <div className="bubble">{done ? '✓' : i + 1}</div>
            {p.label}
          </div>
        );
      })}
    </div>
  );
}

export function ProgressBar({ job }: { job: JobSummary }) {
  const running = job.status === 'Running' || job.status === 'Applying';
  const indeterminate = running && job.total === 0;
  const pct = job.total > 0 ? Math.min(100, Math.round((job.processed / job.total) * 100)) : 0;
  const finished = job.status === 'Completed' || job.status === 'AwaitingReview';
  return (
    <div>
      <div className="progress-track">
        <div
          className={`progress-fill ${indeterminate ? 'indeterminate' : ''}`}
          style={{ width: finished ? '100%' : `${pct}%` }}
        />
      </div>
      <div className="row" style={{ marginTop: 7, fontSize: 12 }}>
        <span className="dim">
          {indeterminate
            ? `${job.processed.toLocaleString()} items discovered…`
            : job.total > 0
              ? `${job.processed.toLocaleString()} / ${job.total.toLocaleString()} (${pct}%)`
              : finished
                ? 'Done'
                : ''}
        </span>
        <span className="spacer" />
        {running && job.currentItem && <span className="mono dim">{job.currentItem}</span>}
      </div>
    </div>
  );
}

export function fmtDate(iso?: string | null): string {
  if (!iso) return '—';
  const d = new Date(iso);
  return d.toLocaleString(undefined, {
    day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit', second: '2-digit'
  });
}

export function duration(job: JobSummary): string {
  if (!job.started) return '—';
  const end = job.finished ? new Date(job.finished).getTime() : Date.now();
  const s = Math.max(0, Math.round((end - new Date(job.started).getTime()) / 1000));
  return s < 60 ? `${s}s` : `${Math.floor(s / 60)}m ${s % 60}s`;
}
