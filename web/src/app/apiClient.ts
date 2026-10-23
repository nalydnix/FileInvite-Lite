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

export interface RequestOptions {
  method?: 'GET' | 'POST'
  body?: unknown
  /** Bearer token, or the portal session token. Held in memory by the caller, never stored. */
  token?: string
  signal?: AbortSignal
}

export async function apiRequest<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const { method = 'GET', body, token, signal } = options

  const headers: Record<string, string> = {}
  if (body !== undefined) headers['Content-Type'] = 'application/json'
  if (token) headers.Authorization = `Bearer ${token}`

  const response = await fetch(`${baseUrl}${path}`, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
    signal,
    // No cookies cross origins: the token is a bearer header (ADR 0003).
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
