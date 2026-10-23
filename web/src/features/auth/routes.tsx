import type { RouteObject } from 'react-router-dom'
import { LoginPage } from './LoginPage'

/** Each feature exports its own routes; app/routes.tsx collects them (docs/architecture.md §8). */
export const authRoutes: RouteObject[] = [{ path: '/login', element: <LoginPage /> }]
