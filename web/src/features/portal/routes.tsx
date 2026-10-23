import type { RouteObject } from 'react-router-dom'
import { PortalSessionProvider } from '../../app/PortalSessionContext'
import { PortalPage } from './PortalPage'

/** The portal has no login and no staff guard: the magic link is the credential. */
export const portalRoutes: RouteObject[] = [
  {
    path: '/portal',
    element: (
      <PortalSessionProvider>
        <PortalPage />
      </PortalSessionProvider>
    ),
  },
]
