import { BriefcaseBusiness } from 'lucide-react'
import { CatalogPage, type CatalogKind } from './catalog-page'

const JOB_TITLES: CatalogKind = {
  title: 'Job titles',
  noun: 'job title',
  description: 'What people’s jobs are called. Each employee can have one.',
  route: '/api/core/v1/job-titles',
  idField: 'jobTitleId',
  icon: BriefcaseBusiness,
}

export function JobTitles() {
  return <CatalogPage kind={JOB_TITLES} />
}
