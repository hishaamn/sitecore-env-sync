import { NavLink, Route, Routes } from 'react-router-dom';
import Dashboard from './pages/Dashboard';
import Environments from './pages/Environments';
import NewSync from './pages/NewSync';
import JobDetail from './pages/JobDetail';

export default function App() {
  return (
    <>
      <nav className="sidebar">
        <div className="logo">
          Site<span>Sync</span>
          <small>Sitecore content synchronisation</small>
        </div>
        <NavLink to="/" end className={({ isActive }) => 'nav' + (isActive ? ' active' : '')}>
          ◈ Dashboard
        </NavLink>
        <NavLink to="/sync/new" className={({ isActive }) => 'nav' + (isActive ? ' active' : '')}>
          ⇄ New sync
        </NavLink>
        <NavLink to="/environments" className={({ isActive }) => 'nav' + (isActive ? ' active' : '')}>
          ⛁ Environments
        </NavLink>
      </nav>
      <main className="main">
        <Routes>
          <Route path="/" element={<Dashboard />} />
          <Route path="/sync/new" element={<NewSync />} />
          <Route path="/environments" element={<Environments />} />
          <Route path="/jobs/:jobId" element={<JobDetail />} />
        </Routes>
      </main>
    </>
  );
}
