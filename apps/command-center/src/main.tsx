import React from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom';
import '@practicex/design-system/styles.css';
import './styles.css';
import { AppShell } from './shell/AppShell';
import { CommandCenterPage } from './views/CommandCenterPage';
import { DocumentDetailPage } from './views/DocumentDetailPage';
import { EntityGraphPage } from './views/EntityGraphPage';
import { LegalAdvisorPage } from './views/LegalAdvisorPage';
import { PortfolioPage } from './views/PortfolioPage';
import { RenewalsPage } from './views/RenewalsPage';
import { ReviewQueuePage } from './views/ReviewQueuePage';
import { SourceDiscoveryPage } from './views/SourceDiscoveryPage';

createRoot(document.getElementById('root') as HTMLElement).render(
  <React.StrictMode>
    <BrowserRouter>
      <Routes>
        <Route element={<AppShell />}>
          <Route index element={<Navigate to="/dashboard" replace />} />
          <Route path="/dashboard" element={<CommandCenterPage />} />
          <Route path="/review" element={<ReviewQueuePage />} />
          <Route path="/sources" element={<SourceDiscoveryPage />} />
          <Route path="/portfolio" element={<PortfolioPage />} />
          <Route path="/portfolio/:assetId" element={<DocumentDetailPage />} />
          <Route path="/contracts" element={<CommandCenterPage />} />
          <Route path="/renewals" element={<RenewalsPage />} />
          <Route path="/legal-advisor" element={<LegalAdvisorPage />} />
          <Route path="/graph" element={<EntityGraphPage />} />
          <Route path="/alerts" element={<CommandCenterPage />} />
          <Route path="/obligations" element={<CommandCenterPage />} />
          <Route path="/rates" element={<CommandCenterPage />} />
          <Route path="/admin" element={<CommandCenterPage />} />
        </Route>
      </Routes>
    </BrowserRouter>
  </React.StrictMode>,
);

