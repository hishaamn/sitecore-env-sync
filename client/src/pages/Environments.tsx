import { FormEvent, useEffect, useState } from 'react';
import { api, ConnectorType, Environment } from '../api';

interface EnvForm {
  id?: string;
  name: string;
  baseUrl: string;
  username: string;
  password: string;
  domain: string;
  databases: string;
  connectorType: ConnectorType;
  color: string;
}

const emptyForm: EnvForm = {
  name: '', baseUrl: '', username: '', password: '', domain: 'sitecore',
  databases: 'master, web', connectorType: 'ItemService', color: '#6c8cff'
};

export default function Environments() {
  const [envs, setEnvs] = useState<Environment[]>([]);
  const [form, setForm] = useState<EnvForm | null>(null);
  const [testResults, setTestResults] = useState<Record<string, { success: boolean; message: string }>>({});
  const [testing, setTesting] = useState<string | null>(null);
  const [error, setError] = useState('');

  const reload = () => api.environments.list().then(setEnvs).catch(e => setError(e.message));
  useEffect(() => { reload(); }, []);

  const startEdit = (e: Environment) => setForm({
    id: e.id, name: e.name, baseUrl: e.baseUrl, username: e.username, password: '',
    domain: e.domain, databases: e.databases.join(', '), connectorType: e.connectorType, color: e.color
  });

  const submit = async (ev: FormEvent) => {
    ev.preventDefault();
    if (!form) return;
    setError('');
    const payload = {
      name: form.name,
      baseUrl: form.baseUrl,
      username: form.username,
      password: form.password,
      domain: form.domain,
      databases: form.databases.split(',').map(s => s.trim()).filter(Boolean),
      connectorType: form.connectorType,
      color: form.color
    };
    try {
      if (form.id) await api.environments.update(form.id, payload);
      else await api.environments.create(payload);
      setForm(null);
      reload();
    } catch (e: any) {
      setError(e.message);
    }
  };

  const test = async (id: string) => {
    setTesting(id);
    try {
      const result = await api.environments.test(id);
      setTestResults(r => ({ ...r, [id]: result }));
    } catch (e: any) {
      setTestResults(r => ({ ...r, [id]: { success: false, message: e.message } }));
    } finally {
      setTesting(null);
    }
  };

  const remove = async (e: Environment) => {
    if (!confirm(`Delete environment "${e.name}"?`)) return;
    await api.environments.remove(e.id);
    reload();
  };

  return (
    <>
      <h1>Environments</h1>
      <p className="subtitle">Each environment is one Sitecore server; pick which databases it exposes.</p>

      {error && <div className="error-banner">{error}</div>}

      {!form && (
        <button onClick={() => setForm({ ...emptyForm })} style={{ marginBottom: 18 }}>
          + Add environment
        </button>
      )}

      {form && (
        <form className="card" style={{ marginBottom: 22, maxWidth: 640 }} onSubmit={submit}>
          <h2 style={{ marginTop: 0 }}>{form.id ? 'Edit environment' : 'New environment'}</h2>
          <label className="field">
            <span className="name">Name</span>
            <input value={form.name} onChange={e => setForm({ ...form, name: e.target.value })}
              placeholder="e.g. PROD Europe" required />
          </label>
          <label className="field">
            <span className="name">Connector</span>
            <select value={form.connectorType}
              onChange={e => setForm({ ...form, connectorType: e.target.value as ConnectorType })}>
              <option value="ItemService">Sitecore Item Service (real environment)</option>
              <option value="Simulated">Simulated (demo, no server needed)</option>
            </select>
          </label>
          {form.connectorType === 'ItemService' && (
            <>
              <label className="field">
                <span className="name">Base URL (Content Management host)</span>
                <input value={form.baseUrl} onChange={e => setForm({ ...form, baseUrl: e.target.value })}
                  placeholder="https://cm.myproject.com" required />
              </label>
              <div className="row">
                <label className="field" style={{ flex: 1 }}>
                  <span className="name">Domain</span>
                  <input value={form.domain} onChange={e => setForm({ ...form, domain: e.target.value })} />
                </label>
                <label className="field" style={{ flex: 1 }}>
                  <span className="name">Username</span>
                  <input value={form.username} onChange={e => setForm({ ...form, username: e.target.value })} />
                </label>
                <label className="field" style={{ flex: 1 }}>
                  <span className="name">Password {form.id && <em>(blank = unchanged)</em>}</span>
                  <input type="password" value={form.password}
                    onChange={e => setForm({ ...form, password: e.target.value })} />
                </label>
              </div>
            </>
          )}
          <div className="row">
            <label className="field" style={{ flex: 2 }}>
              <span className="name">Databases (comma-separated)</span>
              <input value={form.databases} onChange={e => setForm({ ...form, databases: e.target.value })} />
            </label>
            <label className="field" style={{ flex: 1 }}>
              <span className="name">Colour</span>
              <input type="color" value={form.color} style={{ height: 38, padding: 3 }}
                onChange={e => setForm({ ...form, color: e.target.value })} />
            </label>
          </div>
          <div className="row">
            <button type="submit">{form.id ? 'Save changes' : 'Add environment'}</button>
            <button type="button" className="ghost" onClick={() => setForm(null)}>Cancel</button>
          </div>
        </form>
      )}

      <div className="grid cols-2">
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
            <div className="dim mono" style={{ margin: '8px 0', fontSize: 12 }}>{e.baseUrl || '—'}</div>
            <div className="row">
              {e.databases.map(db => <span key={db} className="badge gray">{db}</span>)}
            </div>
            {testResults[e.id] && (
              <div style={{ marginTop: 10, fontSize: 12.5 }}
                className={testResults[e.id].success ? '' : 'error-banner'}>
                <span style={{ color: testResults[e.id].success ? 'var(--green)' : undefined }}>
                  {testResults[e.id].success ? '✓ ' : ''}{testResults[e.id].message}
                </span>
              </div>
            )}
            <div className="row" style={{ marginTop: 14 }}>
              <button className="secondary small" disabled={testing === e.id} onClick={() => test(e.id)}>
                {testing === e.id ? 'Testing…' : 'Test connection'}
              </button>
              <button className="ghost small" onClick={() => startEdit(e)}>Edit</button>
              <span className="spacer" />
              <button className="danger small" onClick={() => remove(e)}>Delete</button>
            </div>
          </div>
        ))}
      </div>
    </>
  );
}
