import type { RouteObject } from 'react-router-dom'
import { RequireStaffAuth } from '../../app/RequireStaffAuth'
import { CreateRequestPage } from './CreateRequestPage'
import { RequestListPage } from './RequestListPage'

/** Each feature exports its own routes; app/routes.tsx collects them (docs/architecture.md §8). */
export const requestRoutes: RouteObject[] = [
  {
    path: '/requests',
    element: (
      <RequireStaffAuth>
        <RequestListPage />
      </RequireStaffAuth>
    ),
  },
  {
    path: '/requests/new',
    element: (
      <RequireStaffAuth>
        <CreateRequestPage />
      </RequireStaffAuth>
    ),
  },
]
