import * as signalR from '@microsoft/signalr';
import { useEffect } from 'react';
import type { JobSummary, LogEntry } from './api';

// One shared connection for the whole app; pages subscribe/unsubscribe to events.
const connection = new signalR.HubConnectionBuilder()
  .withUrl('/hubs/sync')
  .withAutomaticReconnect()
  .configureLogging(signalR.LogLevel.Warning)
  .build();

let started = false;
async function ensureStarted() {
  if (started) return;
  started = true;
  try {
    await connection.start();
  } catch (err) {
    started = false;
    console.error('SignalR connection failed, retrying in 3s', err);
    setTimeout(ensureStarted, 3000);
  }
}

export function useJobProgress(handler: (job: JobSummary) => void) {
  useEffect(() => {
    ensureStarted();
    connection.on('progress', handler);
    return () => connection.off('progress', handler);
  }, [handler]);
}

export function useJobLog(handler: (payload: { jobId: string; entry: LogEntry }) => void) {
  useEffect(() => {
    ensureStarted();
    connection.on('log', handler);
    return () => connection.off('log', handler);
  }, [handler]);
}
