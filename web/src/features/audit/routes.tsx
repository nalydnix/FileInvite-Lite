import type { RouteObject } from 'react-router-dom'
import { RequireStaffAuth } from '../../app/RequireStaffAuth'
import { AuditTrailPage } from './AuditTrailPage'

/** Each feature exports its own routes; app/routes.tsx collects them (docs/architecture.md §8). */
export const auditRoutes: RouteObject[] = [
  {
    path: '/requests/:id/audit',
    element: (
      <RequireStaffAuth>
        <AuditTrailPage />
      </RequireStaffAuth>
    ),
  },
]
