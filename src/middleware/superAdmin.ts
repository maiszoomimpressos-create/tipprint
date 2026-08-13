import { createClient } from '@supabase/supabase-js';

const supabase = createClient(process.env.SUPABASE_URL!, process.env.SUPABASE_ANON_KEY!);

export async function requireSuperAdmin(req, res, next) {
  const { data: user, error } = await supabase
    .from('users')
    .select('role')
    .eq('id', req.headers.userid as string)
    .single();

  if (error || user.role !== 'super_admin') {
    return res.status(403).json({ error: 'Acesso negado' });
  }
  next();
}