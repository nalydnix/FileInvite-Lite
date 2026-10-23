import { describe, expect, it } from 'vitest'
import { readPortalToken } from '../src/app/portalSessionState'

describe('readPortalToken', () => {
  it('reads the token from the fragment (S9.3)', () => {
    expect(readPortalToken('#t=Zm9vYmFy')).toBe('Zm9vYmFy')
  })

  it('keeps base64url characters intact', () => {
    expect(readPortalToken('#t=ab-cd_ef')).toBe('ab-cd_ef')
  })

  it('returns null when there is no usable token', () => {
    expect(readPortalToken('')).toBeNull()
    expect(readPortalToken('#')).toBeNull()
    expect(readPortalToken('#x=1')).toBeNull()
    expect(readPortalToken('#t=')).toBeNull()
  })
})

// The portal's transport — the `Portal` scheme and multipart uploads — is asserted in
// apiClient.test.ts, because apiClient is the one place that talks to the API (web/CLAUDE.md).
