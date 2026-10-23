import { Navigate, type RouteObject } from 'react-router-dom'
import { auditRoutes } from '../features/audit/routes'
import { authRoutes } from '../features/auth/routes'
import { portalRoutes } from '../features/portal/routes'
import { requestRoutes } from '../features/requests/routes'

/**
 * The route table, assembled from each feature's own export. Adding a page means adding it to
 * that feature's routes.tsx and one import line here — nothing else in app/ changes.
 *
 * The review, audit and portal features arrive in Phase 1.
 */
export const routes: RouteObject[] = [
  { path: '/', element: <Navigate to="/requests" replace /> },

  ...authRoutes,
  ...requestRoutes,
  ...auditRoutes,
  ...portalRoutes,

  { path: '*', element: <main className="page"><h1>Not found</h1></main> },
]
