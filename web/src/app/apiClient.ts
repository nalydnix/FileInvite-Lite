/**
 * The only place in the app that talks to the API.
 *
 * Everything goes through here so that the base URL, the bearer header and the problem-details
 * parsing exist exactly once. A component that calls `fetch` directly has bypassed all three —
 * see web/CLAUDE.md.
 */

const baseUrl: string = import.meta.env.VITE_API_URL ?? 'http://localhost:5080'

/** RFC 7807 problem details, as described in docs/api-contract.md. */
export interface ProblemDetails {
  type?: string
  title?: string
  status?: number
  detail?: string
  traceId?: string
  errors?: Record<string, string[]>
}

export class ApiError extends Error {
  readonly status: number
  readonly problem: ProblemDetails

  constructor(status: number, problem: ProblemDetails) {
    // `detail` is written for a person to read; the status code is not.
    super(problem.detail ?? problem.title ?? `Request failed with status ${status}.`)
    this.name = 'ApiError'
    this.status = status
    this.problem = problem
  }

  /** Validation messages for one field, if the server returned any. */
  fieldErrors(field: string): string[] {
    return this.problem.errors?.[field] ?? []
  }
}

/**
 * The `Authorization` scheme word. Two credentials that are never interchangeable, so the caller
 * states which one it holds rather than this file guessing (docs/api-contract.md §Authentication).
 */
export type AuthScheme = 'Bearer' | 'Portal'

export interface RequestOptions {
  method?: 'GET' | 'POST'
  /** JSON-encoded, unless it is `FormData`, which is sent as multipart. */
  body?: unknown
  /** Staff JWT, or the portal session token. Held in memory by the caller, never stored. */
  token?: string
  /** Defaults to `Bearer`; portal endpoints take `Portal`. */
  scheme?: AuthScheme
  signal?: AbortSignal
}

export async function apiRequest<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const { method = 'GET', body, token, scheme = 'Bearer', signal } = options

  const headers: Record<string, string> = {}
  if (token) headers.Authorization = `${scheme} ${token}`

  let payload: BodyInit | undefined
  if (body instanceof FormData) {
    // The browser writes the multipart boundary into Content-Type; setting it here breaks it.
    payload = body
  } else if (body !== undefined) {
    headers['Content-Type'] = 'application/json'
    payload = JSON.stringify(body)
  }

  const response = await fetch(`${baseUrl}${path}`, {
    method,
    headers,
    body: payload,
    signal,
    // No cookies cross origins: the token is an Authorization header (ADR 0003).
    credentials: 'omit',
  })

  if (!response.ok) {
    throw new ApiError(response.status, await readProblem(response))
  }

  if (response.status === 204) {
    return undefined as T
  }

  return (await response.json()) as T
}

async function readProblem(response: Response): Promise<ProblemDetails> {
  try {
    return (await response.json()) as ProblemDetails
  } catch {
    // A proxy or gateway error will not be problem+json. Never show the raw body.
    return { status: response.status, title: 'The server returned an unexpected response.' }
  }
}

export const api = {
  login: (email: string, password: string) =>
    apiRequest<{ token: string; expiresAt: string; name: string }>('/api/auth/login', {
      method: 'POST',
      body: { email, password },
    }),

  health: () => apiRequest<{ status: string }>('/health'),
}
