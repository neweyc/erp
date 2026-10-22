import { MapPin } from 'lucide-react'
import { CatalogPage, type CatalogKind } from './catalog-page'

const LOCATIONS: CatalogKind = {
  title: 'Locations',
  noun: 'location',
  description: 'Where people work. Each employee can have one.',
  route: '/api/core/v1/locations',
  idField: 'locationId',
  icon: MapPin,
}

export function Locations() {
  return <CatalogPage kind={LOCATIONS} />
}
