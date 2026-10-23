import { describe, expect, it, vi, afterEach } from 'vitest'
import { ApiError, apiRequest } from '../src/app/apiClient'

function mockFetch(response: Response) {
  return vi.spyOn(globalThis, 'fetch').mockResolvedValue(response)
}

function problem(status: number, body: unknown) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/problem+json' },
  })
}

describe('apiClient', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('throws an ApiError carrying the problem details', async () => {
    mockFetch(problem(409, { status: 409, title: 'Conflict', detail: 'Item already approved.' }))

    const error = await apiRequest('/api/items/x/approve', { method: 'POST' }).catch((e) => e)

    expect(error).toBeInstanceOf(ApiError)
    expect((error as ApiError).status).toBe(409)
    expect((error as ApiError).message).toBe('Item already approved.')
  })

  it('exposes per-field validation messages', async () => {
    mockFetch(
      problem(400, {
        status: 400,
        title: 'One or more validation errors occurred.',
        errors: { items: ['Items must contain at least one entry.'] },
      }),
    )

    const error = (await apiRequest('/api/requests', { method: 'POST' }).catch(
      (e) => e,
    )) as ApiError

    expect(error.fieldErrors('items')).toEqual(['Items must contain at least one entry.'])
    expect(error.fieldErrors('clientName')).toEqual([])
  })

  it('does not leak a raw body when the response is not problem+json', async () => {
    mockFetch(new Response('<html>502 Bad Gateway — upstream nginx/1.2.3</html>', { status: 502 }))

    const error = (await apiRequest('/health').catch((e) => e)) as ApiError

    expect(error.message).not.toContain('nginx')
    expect(error.message).toBe('The server returned an unexpected response.')
  })

  it('sends the token as a bearer header and no cookies', async () => {
    const fetchMock = mockFetch(new Response(JSON.stringify({ status: 'healthy' }), { status: 200 }))

    await apiRequest('/health', { token: 'the-token' })

    const [, init] = fetchMock.mock.calls[0]
    expect((init?.headers as Record<string, string>).Authorization).toBe('Bearer the-token')
    expect(init?.credentials).toBe('omit')
  })

  // The portal session token is a different credential with a different scheme word, and the two
  // must never be interchangeable (docs/api-contract.md §Authentication, S2.7, S2.8).
  it('authorises a portal call with the Portal scheme, never Bearer', async () => {
    const fetchMock = mockFetch(new Response(JSON.stringify({ ok: true }), { status: 200 }))

    await apiRequest('/api/portal/request', { token: 'session-token', scheme: 'Portal' })

    const [, init] = fetchMock.mock.calls[0]
    expect((init?.headers as Record<string, string>).Authorization).toBe('Portal session-token')
    expect(init?.credentials).toBe('omit')
  })

  it('sends FormData as multipart without forcing a content type', async () => {
    const fetchMock = mockFetch(new Response(JSON.stringify({ id: 'x' }), { status: 201 }))
    const form = new FormData()
    form.append('file', new File(['x'], 'a.pdf'))

    await apiRequest('/api/portal/items/i/files', {
      method: 'POST',
      body: form,
      token: 't',
      scheme: 'Portal',
    })

    const [, init] = fetchMock.mock.calls[0]
    expect(init?.body).toBe(form)
    // The browser writes the boundary into Content-Type; setting it here would break the upload.
    expect(init?.headers as Record<string, string>).not.toHaveProperty('Content-Type')
  })
})
