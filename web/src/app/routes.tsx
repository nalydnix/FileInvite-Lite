import { Navigate, type RouteObject } from 'react-router-dom'
import { authRoutes } from '../features/auth/routes'
import { RequireStaffAuth } from './RequireStaffAuth'

/**
 * The route table, assembled from each feature's own export. Adding a page means adding it to
 * that feature's routes.tsx and one import line here — nothing else in app/ changes.
 *
 * The requests, review, audit and portal features arrive in Phase 0c and Phase 1.
 */
export const routes: RouteObject[] = [
  { path: '/', element: <Navigate to="/requests" replace /> },

  ...authRoutes,

  {
    path: '/requests',
    element: (
      <RequireStaffAuth>
        <main className="page">
          <h1>Requests</h1>
          <p>The request list arrives with the Phase 0c reference slice.</p>
        </main>
      </RequireStaffAuth>
    ),
  },

  { path: '*', element: <main className="page"><h1>Not found</h1></main> },
]
